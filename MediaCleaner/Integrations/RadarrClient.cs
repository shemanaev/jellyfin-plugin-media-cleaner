using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaCleaner.Configuration;

namespace MediaCleaner.Integrations;

internal sealed class RadarrClient(HttpClient httpClient, ConfigArrInstanceNode config)
    : ArrClientBase(httpClient, config)
{
    public Task<ArrConnectionResult> TestConnectionAsync(CancellationToken cancellationToken) =>
        TestConnectionAsync("Radarr", cancellationToken);

    public async Task<IReadOnlyList<RadarrMovieResource>> GetMoviesAsync(CancellationToken cancellationToken) =>
        await GetJsonAsync<List<RadarrMovieResource>>("movie", null, cancellationToken) ?? [];

    public async Task<IReadOnlyList<RadarrMovieResource>> GetMoviesByTmdbIdAsync(int tmdbId, CancellationToken cancellationToken) =>
        await GetJsonAsync<List<RadarrMovieResource>>(
            "movie",
            new Dictionary<string, string?> { ["tmdbId"] = tmdbId.ToString() },
            cancellationToken) ?? [];

    public Task DeleteMovieAsync(int movieId, CancellationToken cancellationToken) =>
        SendAsync(
            HttpMethod.Delete,
            $"movie/{movieId}",
            new Dictionary<string, string?>
            {
                ["deleteFiles"] = "true",
                ["addImportExclusion"] = "true",
            },
            null,
            cancellationToken);
}
