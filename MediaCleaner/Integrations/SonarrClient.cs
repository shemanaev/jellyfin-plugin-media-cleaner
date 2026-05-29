using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaCleaner.Configuration;

namespace MediaCleaner.Integrations;

internal sealed class SonarrClient(HttpClient httpClient, ConfigArrInstanceNode config)
    : ArrClientBase(httpClient, config)
{
    public Task<ArrConnectionResult> TestConnectionAsync(CancellationToken cancellationToken) =>
        TestConnectionAsync("Sonarr", cancellationToken);

    public async Task<IReadOnlyList<SonarrSeriesResource>> GetSeriesAsync(CancellationToken cancellationToken) =>
        await GetJsonAsync<List<SonarrSeriesResource>>("series", null, cancellationToken) ?? [];

    public async Task<IReadOnlyList<SonarrSeriesResource>> GetSeriesByTvdbIdAsync(int tvdbId, CancellationToken cancellationToken) =>
        await GetJsonAsync<List<SonarrSeriesResource>>(
            "series",
            new Dictionary<string, string?> { ["tvdbId"] = tvdbId.ToString() },
            cancellationToken) ?? [];

    public async Task<IReadOnlyList<SonarrEpisodeResource>> GetEpisodesAsync(
        int seriesId,
        int? seasonNumber,
        CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string?>
        {
            ["seriesId"] = seriesId.ToString(),
            ["includeEpisodeFile"] = "true",
        };

        if (seasonNumber.HasValue)
        {
            query["seasonNumber"] = seasonNumber.Value.ToString();
        }

        return await GetJsonAsync<List<SonarrEpisodeResource>>("episode", query, cancellationToken) ?? [];
    }

    public Task SetEpisodesMonitoredAsync(
        IEnumerable<int> episodeIds,
        bool monitored,
        CancellationToken cancellationToken)
    {
        var request = new
        {
            episodeIds = episodeIds.Distinct().ToList(),
            monitored,
        };

        return SendAsync(
            HttpMethod.Put,
            "episode/monitor",
            null,
            CreateJsonContent(request),
            cancellationToken);
    }

    public Task DeleteEpisodeFileAsync(int episodeFileId, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, $"episodefile/{episodeFileId}", null, null, cancellationToken);

    public Task DeleteSeriesAsync(int seriesId, CancellationToken cancellationToken) =>
        SendAsync(
            HttpMethod.Delete,
            $"series/{seriesId}",
            new Dictionary<string, string?>
            {
                ["deleteFiles"] = "true",
                ["addImportListExclusion"] = "true",
            },
            null,
            cancellationToken);
}
