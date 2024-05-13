using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Mime;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using System.Xml;
using System.Xml.Serialization;
using MediaBrowser.Common;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaCleaner.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.Controllers;

[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Route("MediaCleaner")]
public class TroubleshootingController(
    IServiceScopeFactory scopeFactory,
    IApplicationHost applicationHost
) : ControllerBase
{
    [HttpGet("Log")]
    [Produces(MediaTypeNames.Text.Plain)]
    public async Task<string> GetLog()
    {
        using var scope = scopeFactory.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<IUserManager>();
        var libraryManager = scope.ServiceProvider.GetRequiredService<ILibraryManager>();
        var userDataManager = scope.ServiceProvider.GetRequiredService<IUserDataManager>();
        var activityManager = scope.ServiceProvider.GetRequiredService<IActivityManager>();
        var localization = scope.ServiceProvider.GetRequiredService<ILocalizationManager>();
        var fileSystem = scope.ServiceProvider.GetRequiredService<IFileSystem>();
        var progress = new Progress<double>();

        var logOutput = new List<string>();
        var loggerFactory = new LoggerFactory([new TroubleshootingLoggerProvider(new TroubleshootingLoggerConfiguration { Output = logOutput })]);

        var task = new MediaCleanupTask(userManager, loggerFactory, libraryManager, userDataManager, activityManager, localization, fileSystem)
        {
            IsDryRun = true
        };
        await task.ExecuteAsync(progress, default!);

        var pluginConfig = GetPrettyXml(Plugin.Instance!.Configuration);

        var log = $@"* Jellyfin version: {applicationHost.ApplicationVersionString}
* Plugin version: {Plugin.Instance.Version}
<details>
<summary>Configuration</summary>
<pre>
{HttpUtility.HtmlEncode(pluginConfig)}
</pre>
</details>
<details>
<summary>Log</summary>
<pre>
{HttpUtility.HtmlEncode(string.Join('\n', logOutput))}
</pre>
</details>
";

        return log;
    }

    private static string GetPrettyXml(object o)
    {
        using var memoryStream = new MemoryStream();
        var serializer = new XmlSerializer(o.GetType());
        var ns = new XmlSerializerNamespaces(new[] { XmlQualifiedName.Empty });
        var streamWriter = XmlWriter.Create(memoryStream, new()
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            OmitXmlDeclaration = true,
        });
        serializer.Serialize(streamWriter, o, ns);
        return Encoding.UTF8.GetString(memoryStream.ToArray());
    }
}
