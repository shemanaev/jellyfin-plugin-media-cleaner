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
        plan.AuditEntries.Should().BeEmpty();
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(8, false)]
    [InlineData(32, false)]
    [InlineData(1, true)]
    [InlineData(8, true)]
    [InlineData(32, true)]
    public void Plan_RuleCountAndMediaScopeMatrix_CompletesWithinBudget(int ruleCount, bool allMedia)
    {
        var users = new[] { new MediaUser("u1", "one") };
        var items = BuildLibrary(users).ToList();
        var mediaKinds = allMedia ? Enum.GetValues<MediaItemKind>() : [MediaItemKind.Movie];
        var rules = Enumerable.Range(0, ruleCount)
            .Select(index => Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.AddedAge, 10) with
            {
                Id = $"rule-{index}",
                Name = $"rule {index}",
                Filters = Filters(MediaItemKind.Movie) with { MediaKinds = mediaKinds },
            })
            .ToList();
        var request = new CleanupRequest(new CleanupPolicy(rules, false), users, items, IsDryRun: false);

        var stopwatch = Stopwatch.StartNew();
        var plan = Planner().Plan(request);
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        plan.Decisions.Should().NotBeEmpty();
        plan.AuditEntries.Should().BeEmpty();
    }

    [Fact]
    public void Plan_NormalRunAllocatesLessThanSixtyPercentOfAuditHeavyDryRun()
    {
        var users = new[] { new MediaUser("u1", "one") };
        var items = Enumerable.Range(0, 2_000)
            .Select(index => Item($"movie-{index}", MediaItemKind.Movie, users))
            .ToList();
        var rule = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.AddedAge, 10) with
        {
            Filters = Filters(MediaItemKind.Movie) with
            {
                EnableTagFilter = true,
                TagFilterMode = TagMode.Inclusion,
                Tags = ["missing"],
            },
        };
        var policy = new CleanupPolicy([rule], false);
        var normalRequest = new CleanupRequest(policy, users, items, IsDryRun: false);
        var dryRunRequest = normalRequest with { IsDryRun = true };

        _ = Planner().Plan(normalRequest);
        _ = Planner().Plan(dryRunRequest);

        var normalBytes = MeasureMedianAllocatedBytes(normalRequest);
        var dryRunBytes = MeasureMedianAllocatedBytes(dryRunRequest);

        normalBytes.Should().BeLessThan(
            (long)(dryRunBytes * 0.60),
            $"normal planning allocated {normalBytes:N0} bytes while dry-run allocated {dryRunBytes:N0} bytes");
    }

    [Fact]
    public void Matcher_NormalRunEvaluatesTriggerOnlyAfterCheapFiltersPass()
    {
        var user = new MediaUser("u1", "one");
        var items = Enumerable.Range(0, 100)
            .Select(index => Item($"movie-{index}", MediaItemKind.Movie, [user]) with
            {
                Tags = index < 5 ? ["cleanup"] : ["keep"],
            })
            .ToList();
        items.Add(Item("recent-movie", MediaItemKind.Movie, [user]) with
        {
            DateCreated = Now.AddDays(-1),
            Tags = ["cleanup"],
        });
        var rule = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.AddedAge, 10) with
        {
            Filters = Filters(MediaItemKind.Movie) with
            {
                EnableTagFilter = true,
                TagFilterMode = TagMode.Inclusion,
                Tags = ["cleanup"],
            },
        };
        var matcher = new CleanupRuleMatcher(Now, new TestPathMatcher(), new CleanupPolicy([rule], false));
        var audit = new CleanupAuditCollector(false);
        var context = matcher.CreateContext([user], rule, audit)!;

        var matches = matcher.CollectRuleMatches(CleanupCatalogIndex.Create(items), context, audit).ToList();

        matches.Should().HaveCount(5);
        matcher.TriggerEvaluationCount.Should().Be(6);
    }

    [Fact]
    public void Cascade_ThousandEpisodeSeriesUpdatesParentCountersLinearly()
    {
        const int episodeCount = 1_024;
        var episodeIds = Enumerable.Range(0, episodeCount).Select(index => $"e-{index}").ToList();
        var series = new MediaItem(
            "series", MediaItemKind.Series, "series", "series", Now, "/series", "/series", [], [],
            SeriesId: "series", EpisodeIds: episodeIds);
        var season = new MediaItem(
            "season", MediaItemKind.Season, "season", "season", Now, "/series/season", "/series/season", [], [],
            SeriesId: "series", SeasonId: "season", EpisodeIds: episodeIds);
        var episodes = episodeIds.Select(id => new MediaItem(
            id, MediaItemKind.Episode, id, id, Now, $"/series/season/{id}.mkv", "/series", [], [],
            SeriesId: "series", SeasonId: "season")).ToList();
        var byId = episodes.Append(season).Append(series).ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var decisions = episodes.Select(item => CleanupDecisionFactory.Create(
            item, ExpiredKind.AddedAge, [], [], ["rule"])).ToList();
        var planner = new DeletionCascadePlanner(new NoExtraFileProbe());

        var operations = planner.BuildDeletionOperations(
            decisions,
            byId,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new CleanupAuditCollector(false)).ToList();

        operations.Select(operation => operation.ItemId)
            .Should().ContainInOrder(episodeIds.Concat(["season", "series"]));
        planner.EpisodeCounterUpdateCount.Should().Be(episodeCount * 2);
    }

    [Fact]
    public void DecisionAccumulatorKeepsLatestPlaybackWithoutChangingFirstSeenOrder()
    {
        var item = Item("movie", MediaItemKind.Movie, []);
        var rule = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.Played, 10) with
        {
            Actions = new CleanupRuleActions(CleanupRuleActionKind.Delete, MarkAsUnplayed: true),
        };
        var accumulator = new DeleteDecisionAccumulator();
        accumulator.Add(new RuleMatch(rule, item, ExpiredKind.Played,
            [new PlaybackState("u1", Now.AddDays(-20), true, false, false)]));
        accumulator.Add(new RuleMatch(rule, item, ExpiredKind.Played,
            [new PlaybackState("u1", Now.AddDays(-10), true, false, false)]));
        accumulator.Add(new RuleMatch(rule, item, ExpiredKind.Played,
            [new PlaybackState("u1", Now.AddDays(-30), true, false, false)]));

        var decision = accumulator.BuildDecisions(new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Single();

        decision.Playback.Should().ContainSingle(x => x.LastPlayedDate == Now.AddDays(-10));
        decision.MarkUnplayedUserIds.Should().Equal("u1");
        decision.MatchedRules.Should().Equal(rule.Name);
    }

    [Fact]
    public void CascadeMetricsStartAtZero_AndProtectedSeasonBlocksSeriesPromotion()
    {
        var episode = new MediaItem(
            "episode", MediaItemKind.Episode, "episode", "episode", Now, "/episode", "/", [], [],
            SeriesId: "series", SeasonId: "season");
        var season = new MediaItem(
            "season", MediaItemKind.Season, "season", "season", Now, "/season", "/", [], [],
            SeriesId: "series", SeasonId: "season", EpisodeIds: ["episode"]);
        var series = new MediaItem(
            "series", MediaItemKind.Series, "series", "series", Now, "/series", "/", [], [],
            SeriesId: "series", EpisodeIds: ["episode"], SeasonIds: ["season"]);
        var byId = new[] { episode, season, series }.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var decision = CleanupDecisionFactory.Create(episode, ExpiredKind.AddedAge, [], [], ["rule"]);
        var planner = new DeletionCascadePlanner(new NoExtraFileProbe());
        planner.EpisodeCounterUpdateCount.Should().Be(0);

        var operations = planner.BuildDeletionOperations(
            [decision],
            byId,
            new HashSet<string>(["season"], StringComparer.OrdinalIgnoreCase),
            new CleanupAuditCollector(false)).ToList();

        operations.Should().ContainSingle(x => x.ItemId == "episode");
        operations.Should().NotContain(x => x.ItemId == "series");
        planner.EpisodeCounterUpdateCount.Should().Be(2);
    }

    private static long MeasureMedianAllocatedBytes(CleanupRequest request)
    {
        var measurements = new long[5];
        for (var index = 0; index < measurements.Length; index++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            _ = Planner().Plan(request);
            measurements[index] = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Array.Sort(measurements);
        return measurements[measurements.Length / 2];
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
