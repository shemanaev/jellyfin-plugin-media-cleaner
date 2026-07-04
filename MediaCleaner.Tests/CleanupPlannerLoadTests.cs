using System.Diagnostics;
using FluentAssertions;
using MediaCleaner.Core;

namespace MediaCleaner.Tests;

public class CleanupPlannerLoadTests
{
    private const int ProgramCount = 72;
    private const int EpisodeCount = 3253;
    private static readonly DateTime Now = new(2026, 07, 04, 12, 0, 0, DateTimeKind.Utc);

    [Fact(Timeout = 10_000)]
    public async Task Plan_LoadShapedLibraryAcrossAllMediaKinds_CompletesWithinBudget()
    {
        var users = new[]
        {
            new MediaUser("u1", "one"),
            new MediaUser("u2", "two"),
            new MediaUser("u3", "three"),
        };
        var items = BuildLibrary(users).ToList();
        items.Count(x => x.Kind == MediaItemKind.Series).Should().Be(ProgramCount);
        items.Count(x => x.Kind == MediaItemKind.Episode).Should().Be(EpisodeCount);

        var policy = new CleanupPolicy(
            [
                Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.Played, 10),
                Rule(MediaItemKind.Video, CleanupRuleTriggerKind.Played, 10),
                Rule(MediaItemKind.Audio, CleanupRuleTriggerKind.Played, 10),
                Rule(MediaItemKind.AudioBook, CleanupRuleTriggerKind.Played, 10),
                Rule(MediaItemKind.Other, CleanupRuleTriggerKind.AddedAge, 10),
                Rule(MediaItemKind.Season, CleanupRuleTriggerKind.AddedAge, 10),
                Rule(MediaItemKind.Series, CleanupRuleTriggerKind.AddedAge, 10),
                Rule(MediaItemKind.Episode, CleanupRuleTriggerKind.Played, 10) with
                {
                    Filters = Filters(MediaItemKind.Episode) with
                    {
                        EnableTagFilter = true,
                        TagFilterMode = TagMode.Inclusion,
                        Tags = ["cleanup"],
                        DeleteEpisodes = SeriesDeleteKind.Episode,
                        KeepSeriesKind = SeriesKeepKind.Last,
                    },
                },
            ],
            AllowDeleteIfPlayedBeforeAdded: false);

        var stopwatch = Stopwatch.StartNew();
        var plan = await Task.Run(() => Planner().Plan(new CleanupRequest(policy, users, items, IsDryRun: false)));
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        plan.Decisions.Select(x => x.Item.Kind)
            .Should()
            .Contain(Enum.GetValues<MediaItemKind>(), "the load scenario should exercise every media kind");
        plan.AuditEntries.Should().NotBeEmpty();
    }

    private static IEnumerable<MediaItem> BuildLibrary(IReadOnlyList<MediaUser> users)
    {
        foreach (var kind in new[]
        {
            MediaItemKind.Movie,
            MediaItemKind.Video,
            MediaItemKind.Audio,
            MediaItemKind.AudioBook,
            MediaItemKind.Other,
        })
        {
            for (var i = 0; i < 200; i++)
            {
                yield return Item($"{kind}-{i}", kind, users);
            }
        }

        for (var seriesIndex = 0; seriesIndex < ProgramCount; seriesIndex++)
        {
            var seriesId = $"series-{seriesIndex}";
            var seasonId = $"season-{seriesIndex}";
            var episodeIds = Enumerable.Range(0, GetEpisodeCount(seriesIndex))
                .Select(episodeIndex => $"episode-{seriesIndex}-{episodeIndex}")
                .ToList();

            yield return new MediaItem(
                Id: seriesId,
                Kind: MediaItemKind.Series,
                Name: seriesId,
                FullName: seriesId,
                DateCreated: Now.AddDays(-60),
                Path: $"/media/{seriesId}",
                LocationPath: $"/media/{seriesId}",
                Tags: ["cleanup"],
                Playback: [],
                SeriesId: seriesId,
                SeriesName: seriesId,
                EpisodeIds: episodeIds,
                SeasonIds: [seasonId]);

            yield return new MediaItem(
                Id: seasonId,
                Kind: MediaItemKind.Season,
                Name: seasonId,
                FullName: $"{seriesId} | {seasonId}",
                DateCreated: Now.AddDays(-60),
                Path: $"/media/{seriesId}/{seasonId}",
                LocationPath: $"/media/{seriesId}/{seasonId}",
                Tags: ["cleanup"],
                Playback: [],
                SeriesId: seriesId,
                SeasonId: seasonId,
                SeriesName: seriesId,
                SeasonName: seasonId,
                ParentIndexNumber: 1,
                IndexNumber: 1,
                EpisodeIds: episodeIds,
                SeasonEpisodeIds: episodeIds,
                SeriesEpisodeIds: episodeIds);

            for (var episodeIndex = 0; episodeIndex < episodeIds.Count; episodeIndex++)
            {
                var episodeId = episodeIds[episodeIndex];
                yield return new MediaItem(
                    Id: episodeId,
                    Kind: MediaItemKind.Episode,
                    Name: episodeId,
                    FullName: $"{seriesId} | S01E{episodeIndex + 1:00} | {episodeId}",
                    DateCreated: Now.AddDays(-60),
                    Path: $"/media/{seriesId}/{seasonId}/{episodeId}.mkv",
                    LocationPath: $"/media/{seriesId}",
                    Tags: ["cleanup"],
                    Playback: Playback(users),
                    SeriesId: seriesId,
                    SeasonId: seasonId,
                    SeriesName: seriesId,
                    SeasonName: seasonId,
                    ParentIndexNumber: 1,
                    IndexNumber: episodeIndex + 1,
                    FirstEpisodeId: episodeIds[0],
                    LastEpisodeId: episodeIds[^1],
                    FirstSeasonId: seasonId,
                    LastSeasonId: seasonId);
            }
        }
    }

    private static int GetEpisodeCount(int seriesIndex) =>
        EpisodeCount / ProgramCount + (seriesIndex < EpisodeCount % ProgramCount ? 1 : 0);

    private static MediaItem Item(string id, MediaItemKind kind, IReadOnlyList<MediaUser> users) =>
        new(
            Id: id,
            Kind: kind,
            Name: id,
            FullName: id,
            DateCreated: Now.AddDays(-60),
            Path: $"/media/{id}",
            LocationPath: $"/media",
            Tags: [],
            Playback: Playback(users));

    private static IReadOnlyList<PlaybackState> Playback(IReadOnlyList<MediaUser> users) =>
        users.Select(user => new PlaybackState(
                user.Id,
                Now.AddDays(-30),
                IsPlayed: true,
                IsWatching: false,
                IsFavorite: false,
                UserName: user.Username))
            .ToList();

    private static CleanupPlanner Planner() =>
        new(new FixedClock(), new TestPathMatcher(), new NoExtraFileProbe());

    private static CleanupRule Rule(MediaItemKind kind, CleanupRuleTriggerKind triggerKind, int days) => new(
        Id: $"{kind}-{triggerKind}",
        Name: $"{kind} {triggerKind}",
        Enabled: true,
        Trigger: new CleanupRuleTrigger(triggerKind, days),
        Filters: Filters(kind),
        Actions: new CleanupRuleActions(CleanupRuleActionKind.Delete, false));

    private static CleanupRuleFilters Filters(MediaItemKind kind) => new(
        MediaKinds: [kind],
        UserIds: [],
        UsersMode: UsersListMode.Ignore,
        FavoriteUserIds: [],
        FavoriteUsersMode: UsersListMode.Ignore,
        FavoriteFilter: RuleFavoriteFilterKind.Ignore,
        Locations: [],
        LocationsMode: LocationsListMode.Exclude,
        EnableTagFilter: false,
        TagFilterMode: TagMode.Exclusion,
        Tags: [],
        DeleteEpisodes: SeriesDeleteKind.Episode,
        KeepSeriesKind: SeriesKeepKind.None);

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Now;
    }

    private sealed class TestPathMatcher : IPathMatcher
    {
        public bool ContainsSubPath(string parentPath, string path) =>
            path.StartsWith(parentPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }
}
