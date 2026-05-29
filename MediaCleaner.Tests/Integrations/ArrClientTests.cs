using System.Net;
using System.Net.Http;
using System.Text.Json;
using MediaCleaner.Configuration;
using MediaCleaner.Integrations;
using Xunit;

namespace MediaCleaner.Tests.Integrations;

public class ArrClientTests
{
    [Fact]
    public async Task Radarr_delete_movie_uses_file_delete_and_import_exclusion()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new RadarrClient(
            new HttpClient(handler),
            new ConfigArrInstanceNode(true, "http://radarr:7878/", "secret-key", 30));

        await client.DeleteMovieAsync(42, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal(
            "http://radarr:7878/api/v3/movie/42?deleteFiles=true&addImportExclusion=true",
            request.RequestUri!.ToString());
        Assert.Equal("secret-key", request.Headers.GetValues("X-Api-Key").Single());
    }

    [Fact]
    public async Task Sonarr_unmonitors_episodes_before_file_deletion()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new SonarrClient(
            new HttpClient(handler),
            new ConfigArrInstanceNode(true, "http://sonarr:8989", "secret-key", 30));

        await client.SetEpisodesMonitoredAsync([12, 13], false, CancellationToken.None);
        await client.DeleteEpisodeFileAsync(99, CancellationToken.None);

        Assert.Collection(
            handler.Requests,
            monitorRequest =>
            {
                Assert.Equal(HttpMethod.Put, monitorRequest.Method);
                Assert.Equal("http://sonarr:8989/api/v3/episode/monitor", monitorRequest.RequestUri!.ToString());
                var json = monitorRequest.Content!.ReadAsStringAsync().Result;
                using var document = JsonDocument.Parse(json);
                Assert.False(document.RootElement.GetProperty("monitored").GetBoolean());
                Assert.Equal([12, 13], document.RootElement.GetProperty("episodeIds").EnumerateArray().Select(x => x.GetInt32()));
            },
            deleteRequest =>
            {
                Assert.Equal(HttpMethod.Delete, deleteRequest.Method);
                Assert.Equal("http://sonarr:8989/api/v3/episodefile/99", deleteRequest.RequestUri!.ToString());
            });
    }

    [Fact]
    public void Deletion_planner_only_delegates_arr_managed_media()
    {
        Assert.True(ArrDeletionPlanner.CanDelegate(Jellyfin.Data.Enums.BaseItemKind.Movie));
        Assert.True(ArrDeletionPlanner.CanDelegate(Jellyfin.Data.Enums.BaseItemKind.Series));
        Assert.True(ArrDeletionPlanner.CanDelegate(Jellyfin.Data.Enums.BaseItemKind.Season));
        Assert.True(ArrDeletionPlanner.CanDelegate(Jellyfin.Data.Enums.BaseItemKind.Episode));
        Assert.False(ArrDeletionPlanner.CanDelegate(Jellyfin.Data.Enums.BaseItemKind.Video));
        Assert.False(ArrDeletionPlanner.CanDelegate(Jellyfin.Data.Enums.BaseItemKind.Audio));
        Assert.False(ArrDeletionPlanner.CanDelegate(Jellyfin.Data.Enums.BaseItemKind.AudioBook));
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
}
