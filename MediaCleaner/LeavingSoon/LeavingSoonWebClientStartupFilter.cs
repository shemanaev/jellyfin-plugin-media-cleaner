using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace MediaCleaner.LeavingSoon;

internal sealed class LeavingSoonWebClientStartupFilter : IStartupFilter
{
    private readonly LeavingSoonWebClientInstaller _installer;
    private readonly ILogger<LeavingSoonWebClientStartupFilter> _logger;

    public LeavingSoonWebClientStartupFilter(
        LeavingSoonWebClientInstaller installer,
        ILogger<LeavingSoonWebClientStartupFilter> logger)
    {
        _installer = installer;
        _logger = logger;
        _installer.MarkStartupFilterAvailable();
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(InjectAsync);
        next(app);
    };

    private async Task InjectAsync(HttpContext context, Func<Task> nextMiddleware)
    {
        if (!_installer.ShouldUseStartupFilter || !IsIndexHtmlRequest(context.Request))
        {
            await nextMiddleware().ConfigureAwait(false);
            return;
        }

        var requestHeaders = context.Request.Headers;
        var acceptEncoding = requestHeaders.AcceptEncoding;
        var ifNoneMatch = requestHeaders.IfNoneMatch;
        var ifModifiedSince = requestHeaders.IfModifiedSince;
        requestHeaders.Remove("Accept-Encoding");
        requestHeaders.Remove("If-None-Match");
        requestHeaders.Remove("If-Modified-Since");

        var originalBody = context.Response.Body;
        await using var captured = new MemoryStream();
        context.Response.Body = captured;
        try
        {
            await nextMiddleware().ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
            RestoreHeader(requestHeaders, "Accept-Encoding", acceptEncoding);
            RestoreHeader(requestHeaders, "If-None-Match", ifNoneMatch);
            RestoreHeader(requestHeaders, "If-Modified-Since", ifModifiedSince);
        }

        var originalBytes = captured.ToArray();
        if (!_installer.ShouldUseStartupFilter ||
            context.Response.StatusCode < StatusCodes.Status200OK ||
            context.Response.StatusCode >= StatusCodes.Status300MultipleChoices ||
            !(context.Response.ContentType ?? string.Empty).Contains("html", StringComparison.OrdinalIgnoreCase) ||
            context.Response.Headers.ContainsKey("Content-Encoding"))
        {
            await originalBody.WriteAsync(originalBytes).ConfigureAwait(false);
            return;
        }

        try
        {
            var html = Encoding.UTF8.GetString(originalBytes);
            LeavingSoonWebClientInstaller.EnsureScriptInstalled(html, out var updated);
            var updatedBytes = Encoding.UTF8.GetBytes(updated);
            context.Response.ContentLength = updatedBytes.Length;
            context.Response.Headers.Remove("ETag");
            context.Response.Headers.Remove("Last-Modified");
            context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.Expires = "0";
            await originalBody.WriteAsync(updatedBytes).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not inject the Leaving Soon script into the served Jellyfin web index.");
            context.Response.ContentLength = originalBytes.Length;
            await originalBody.WriteAsync(originalBytes).ConfigureAwait(false);
        }
    }

    internal static bool IsIndexHtmlRequest(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method))
            return false;

        var path = request.Path.Value ?? string.Empty;
        return path.Equals("/", StringComparison.Ordinal) ||
            path.Equals("/web", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/web", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/web/", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase);
    }

    private static void RestoreHeader(IHeaderDictionary headers, string name, StringValues value)
    {
        if (value.Count > 0)
            headers[name] = value;
    }
}
