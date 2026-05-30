using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Common.Api;
using MediaCleaner.Configuration;
using MediaCleaner.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.Controllers;

[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Route("MediaCleaner/Integrations")]
public class IntegrationsController(ILogger<IntegrationsController> logger) : ControllerBase
{
    [HttpGet("Radarr/Test")]
    public async Task<ArrConnectionResult> TestRadarr(CancellationToken cancellationToken)
    {
        var config = StructuredConfig.CreateFromConfiguration(Plugin.Instance!.Configuration);
        logger.LogInformation("Testing Radarr connection to {BaseUrl}", config.Arr.Radarr.BaseUrl);

        using var httpClient = new HttpClient();
        var client = new RadarrClient(httpClient, config.Arr.Radarr);
        var result = await client.TestConnectionAsync(cancellationToken);
        LogConnectionResult("Radarr", result);
        return result;
    }

    [HttpGet("Sonarr/Test")]
    public async Task<ArrConnectionResult> TestSonarr(CancellationToken cancellationToken)
    {
        var config = StructuredConfig.CreateFromConfiguration(Plugin.Instance!.Configuration);
        logger.LogInformation("Testing Sonarr connection to {BaseUrl}", config.Arr.Sonarr.BaseUrl);

        using var httpClient = new HttpClient();
        var client = new SonarrClient(httpClient, config.Arr.Sonarr);
        var result = await client.TestConnectionAsync(cancellationToken);
        LogConnectionResult("Sonarr", result);
        return result;
    }

    private void LogConnectionResult(string serviceName, ArrConnectionResult result)
    {
        if (result.Success)
        {
            logger.LogInformation(
                "{ServiceName} connection test succeeded. Version: {Version}",
                serviceName,
                result.Version ?? "unknown");
            return;
        }

        logger.LogWarning("{ServiceName} connection test failed: {Message}", serviceName, result.Message);
    }
}
