using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Common.Api;
using MediaCleaner.Configuration;
using MediaCleaner.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MediaCleaner.Controllers;

[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Route("MediaCleaner/Integrations")]
public class IntegrationsController : ControllerBase
{
    [HttpGet("Radarr/Test")]
    public Task<ArrConnectionResult> TestRadarr(CancellationToken cancellationToken)
    {
        var config = StructuredConfig.CreateFromConfiguration(Plugin.Instance!.Configuration);
        var client = new RadarrClient(new HttpClient(), config.Arr.Radarr);
        return client.TestConnectionAsync(cancellationToken);
    }

    [HttpGet("Sonarr/Test")]
    public Task<ArrConnectionResult> TestSonarr(CancellationToken cancellationToken)
    {
        var config = StructuredConfig.CreateFromConfiguration(Plugin.Instance!.Configuration);
        var client = new SonarrClient(new HttpClient(), config.Arr.Sonarr);
        return client.TestConnectionAsync(cancellationToken);
    }
}
