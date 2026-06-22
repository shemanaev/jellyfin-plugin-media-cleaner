using FluentAssertions;
using MediaCleaner.Core;

namespace MediaCleaner.Tests;

public class CleanupPlannerAdditionalTests
{
    private static readonly DateTime Now = new(2026, 06, 22, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Plan_SeriesModeRequiresEveryEpisode_AndExtraFilesBlockSeriesCascade()
    {
        var rule = Rule(MediaItemKind.Episode, CleanupRuleTriggerKind.Played, 10) with
        {
            Filters = Filters(MediaItemKind.Episode) with { DeleteEpisodes = SeriesDeleteKind.Episode }
        };
        var episode = Episode("e1", "s1", "show1", Playback("u1", Now.AddDays(-20), true)) with { SeriesEpisodeIds = ["e1", "e2"] };
        Planner().Plan(new CleanupRequest(Policy(rule), [User("u1")], [episode], false)).Decisions.Should().ContainSingle(x => x.Item.Id == "e1");

        var e2 = Episode("e2", "s1", "show1", Playback("u1", Now.AddDays(-20), true)) with { SeriesEpisodeIds = ["e1", "e2"] };
        var series = Series("show1", ["e1", "e2"]);
        var blocked = Planner(new BlockingExtraFileProbe()).Plan(new CleanupRequest(Policy(rule), [User("u1")], [episode, e2, series], false));

        blocked.Decisions.Should().HaveCount(2);
        blocked.Deletions.Select(x => x.ItemId).Should().NotContain("show1");
        blocked.AuditEntries.Should().Contain(x =>
            x.ItemId == "show1" &&
            x.Stage == CleanupAuditStage.DeletionCascade &&
            x.Outcome == CleanupAuditOutcome.Blocked &&
            x.Reason.Contains("extra files"));
    }

    [Fact]
    public void Ports_ImplementSystemClockPathMatcherAndNoExtraFileProbe()
    {
        new SystemClock().UtcNow.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        new OrdinalPathMatcher().ContainsSubPath(@"D:\Media", @"D:\Media\Movie\file.mkv").Should().BeTrue();
        new NoExtraFileProbe().HasBlockingExtraFiles(Movie("m1")).Should().BeFalse();
    }

    [Theory]
    [InlineData("played")]
    [InlineData("favorite")]
    [InlineData("location")]
    [InlineData("tag")]
    [InlineData("series")]
    [InlineData("action")]
    public void Plan_RejectsUnsupportedEnumValues(string invalidSetting)
    {
        var rule = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.Played, 10);
        if (invalidSetting == "played")
        {
            rule = rule with { Trigger = rule.Trigger with { PlayedKeepKind = (PlayedKeepKind)999 } };
        }
        else if (invalidSetting == "favorite")
        {
            rule = rule with { Filters = rule.Filters with { FavoriteFilter = (RuleFavoriteFilterKind)999 } };
        }
        else if (invalidSetting == "location")
        {
            rule = rule with { Filters = rule.Filters with { Locations = ["/media"], LocationsMode = (LocationsListMode)999 } };
        }
        else if (invalidSetting == "tag")
        {
            rule = rule with { Filters = rule.Filters with { EnableTagFilter = true, Tags = ["keep"], TagFilterMode = (TagMode)999 } };
        }
        else if (invalidSetting == "series")
        {
            rule = Rule(MediaItemKind.Episode, CleanupRuleTriggerKind.Played, 10) with
            {
                Filters = Filters(MediaItemKind.Episode) with { DeleteEpisodes = (SeriesDeleteKind)999 }
            };
        }
        else
        {
            rule = rule with { Actions = new((CleanupRuleActionKind)999, false) };
        }

        var item = rule.Filters.MediaKinds.Contains(MediaItemKind.Episode)
            ? Episode("e1", "s1", "show1", Playback("u1", Now.AddDays(-20), true))
            : Movie("m1", Playback("u1", Now.AddDays(-20), true));
        var act = () => Planner().Plan(new CleanupRequest(Policy(rule), [User("u1")], [item], false));

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Plan_IgnoresSnapshotsWithoutUserData()
    {
        var rule = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.Played, 10);
        var movie = Movie("m1", Playback("u1", Now.AddDays(-20), true) with { HasUserData = false });

        Planner().Plan(new CleanupRequest(Policy(rule), [User("u1")], [movie], false)).Decisions.Should().BeEmpty();
    }

    [Fact]
    public void Plan_UsesUserNameInDecisionNotification()
    {
        var rule = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.Played, 10);
        var movie = Movie("m1", Playback("u1", Now.AddDays(-20), true) with { UserName = "nis" });

        var plan = Planner().Plan(new CleanupRequest(Policy(rule), [User("u1")], [movie], false));

        plan.Decisions.Should().ContainSingle();
        plan.Decisions[0].Reason.Should().Contain("nis");
        plan.Decisions[0].Notification.ShortOverview.Should().Contain("nis");
    }

    private static CleanupPlanner Planner(IExtraFileProbe? extraFileProbe = null) =>
        new(new FixedClock(Now), new TestPathMatcher(), extraFileProbe ?? new NoExtraFileProbe());

    private static CleanupPolicy Policy(params CleanupRule[] rules) => new(rules, false);

    private static CleanupRule Rule(MediaItemKind kind, CleanupRuleTriggerKind triggerKind, int days) => new(
        Id: Guid.NewGuid().ToString("N"),
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
        DeleteEpisodes: SeriesDeleteKind.Season,
        KeepSeriesKind: SeriesKeepKind.None);

    private static MediaUser User(string id) => new(id, id);

    private static PlaybackState Playback(string userId, DateTime? lastPlayedDate, bool isPlayed, bool isWatching = false, bool isFavorite = false) =>
        new(userId, lastPlayedDate, isPlayed, isWatching, isFavorite);

    private static MediaItem Movie(string id, params PlaybackState[] playback) =>
        new(id, MediaItemKind.Movie, id, id, Now.AddDays(-30), $"/media/{id}.mkv", $"/media/{id}.mkv", [], playback);

    private static MediaItem Episode(string id, string seasonId, string seriesId, params PlaybackState[] playback) =>
        new(id, MediaItemKind.Episode, id, id, Now.AddDays(-30), $"/media/{id}.mkv", $"/media/{id}.mkv", [], playback, seriesId, seasonId, seriesId, seasonId, 1, id == "e1" ? 1 : 2);

    private static MediaItem Series(string id, IReadOnlyList<string> episodeIds) =>
        new(id, MediaItemKind.Series, id, id, Now.AddDays(-30), $"/media/{id}", $"/media/{id}", [], [], id, null, id, null, EpisodeIds: episodeIds);

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private sealed class TestPathMatcher : IPathMatcher
    {
        public bool ContainsSubPath(string parentPath, string path) =>
            path.StartsWith(parentPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class BlockingExtraFileProbe : IExtraFileProbe
    {
        public bool HasBlockingExtraFiles(MediaItem item) => item.Kind == MediaItemKind.Series;
    }
}
