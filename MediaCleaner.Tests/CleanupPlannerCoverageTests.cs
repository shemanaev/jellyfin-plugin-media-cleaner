using FluentAssertions;
using MediaCleaner.Core;

namespace MediaCleaner.Tests;

public class CleanupPlannerCoverageTests
{
    private static readonly DateTime Now = new(2026, 06, 22, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(RuleFavoriteFilterKind.FavoriteByAnyUser, true, false, true)]
    [InlineData(RuleFavoriteFilterKind.FavoriteByAllUsers, true, true, true)]
    [InlineData(RuleFavoriteFilterKind.FavoriteByAllUsers, true, false, false)]
    [InlineData(RuleFavoriteFilterKind.NotFavoriteByAnyUser, false, false, true)]
    [InlineData(RuleFavoriteFilterKind.NotFavoriteByAnyUser, true, false, false)]
    [InlineData(RuleFavoriteFilterKind.NotFavoriteByAllUsers, true, false, true)]
    [InlineData(RuleFavoriteFilterKind.NotFavoriteByAllUsers, true, true, false)]
    public void Plan_CoversFavoriteFilterBranches(RuleFavoriteFilterKind filter, bool u1Favorite, bool u2Favorite, bool expected)
    {
        var rule = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.Played, 10) with
        {
            Filters = Filters(MediaItemKind.Movie) with { FavoriteFilter = filter }
        };
        var movie = Movie("m1",
            Playback("u1", Now.AddDays(-20), true, isFavorite: u1Favorite),
            Playback("u2", Now.AddDays(-20), true, isFavorite: u2Favorite));

        var plan = Planner().Plan(new CleanupRequest(Policy(rule), [User("u1"), User("u2")], [movie], false));

        plan.Decisions.Any().Should().Be(expected);
    }

    [Fact]
    public void Plan_CoversUserAndLocationEdgeBranches()
    {
        var acknowledged = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.Played, 10) with
        {
            Filters = Filters(MediaItemKind.Movie) with { UserIds = ["u1"], UsersMode = UsersListMode.Acknowledge }
        };
        var noUsers = acknowledged with { Filters = acknowledged.Filters with { UserIds = ["missing"] } };
        var missingPathExclude = Rule(MediaItemKind.Video, CleanupRuleTriggerKind.Played, 10) with
        {
            Filters = Filters(MediaItemKind.Video) with { Locations = ["/media"], LocationsMode = LocationsListMode.Exclude }
        };
        var missingPathInclude = missingPathExclude with
        {
            Filters = missingPathExclude.Filters with { LocationsMode = LocationsListMode.Include }
        };
        var movie = Movie("m1", Playback("u1", Now.AddDays(-20), true), Playback("u2", Now.AddDays(-1), true));
        var video = Movie("v1", Playback("u1", Now.AddDays(-20), true)) with { Kind = MediaItemKind.Video, Path = null, LocationPath = null };

        Planner().Plan(new CleanupRequest(Policy(acknowledged), [User("u1"), User("u2")], [movie], false)).Decisions.Should().ContainSingle();
        Planner().Plan(new CleanupRequest(Policy(noUsers), [User("u1")], [movie], false)).Decisions.Should().BeEmpty();
        Planner().Plan(new CleanupRequest(Policy(missingPathExclude), [User("u1")], [video], false)).Decisions.Should().ContainSingle();
        Planner().Plan(new CleanupRequest(Policy(missingPathInclude), [User("u1")], [video], false)).Decisions.Should().BeEmpty();
    }

    [Fact]
    public void Plan_AddedAgeRestrictsPlaybackToSelectedUsers()
    {
        var rule = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.AddedAge, 10) with
        {
            Filters = Filters(MediaItemKind.Movie) with
            {
                UserIds = ["u1"],
                UsersMode = UsersListMode.Acknowledge,
            },
        };
        var movie = Movie(
            "m1",
            Playback("u1", Now.AddDays(-20), true),
            Playback("u2", Now.AddDays(-20), true));

        var plan = Planner().Plan(new CleanupRequest(Policy(rule), [User("u1"), User("u2")], [movie], false));

        plan.Decisions.Should().ContainSingle()
            .Which.Playback.Should().ContainSingle(x => x.UserId == "u1");
    }
    [Fact]
    public void Plan_CoversTagFilterBranches()
    {
        var disabled = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.Played, 10) with
        {
            Filters = Filters(MediaItemKind.Movie) with { EnableTagFilter = false, Tags = ["keep"] }
        };
        var emptyExclusionTags = disabled with { Filters = disabled.Filters with { EnableTagFilter = true, TagFilterMode = TagMode.Exclusion, Tags = [] } };
        var emptyInclusionTags = disabled with { Filters = disabled.Filters with { EnableTagFilter = true, TagFilterMode = TagMode.Inclusion, Tags = [] } };
        var invalidEmptyTags = disabled with { Filters = disabled.Filters with { EnableTagFilter = true, TagFilterMode = (TagMode)999, Tags = [] } };
        var exclusion = disabled with { Filters = disabled.Filters with { EnableTagFilter = true, TagFilterMode = TagMode.Exclusion, Tags = ["keep"] } };
        var inclusion = disabled with { Filters = disabled.Filters with { EnableTagFilter = true, TagFilterMode = TagMode.Inclusion, Tags = ["keep"] } };
        var tagged = Movie("m1", Playback("u1", Now.AddDays(-20), true)) with { Tags = ["keep"] };
        var untagged = Movie("m2", Playback("u1", Now.AddDays(-20), true));

        Planner().Plan(new CleanupRequest(Policy(disabled), [User("u1")], [tagged], false)).Decisions.Should().ContainSingle();
        Planner().Plan(new CleanupRequest(Policy(emptyExclusionTags), [User("u1")], [tagged], false)).Decisions.Should().ContainSingle();
        Planner().Plan(new CleanupRequest(Policy(emptyInclusionTags), [User("u1")], [tagged], false)).Decisions.Should().BeEmpty();
        FluentActions.Invoking(() => Planner().Plan(new CleanupRequest(Policy(invalidEmptyTags), [User("u1")], [tagged], false)))
            .Should().Throw<NotSupportedException>();
        Planner().Plan(new CleanupRequest(Policy(exclusion), [User("u1")], [tagged], false)).Decisions.Should().BeEmpty();
        Planner().Plan(new CleanupRequest(Policy(inclusion), [User("u1")], [tagged, untagged], false)).Decisions.Should().ContainSingle(x => x.Item.Id == "m1");
    }

    [Fact]
    public void Plan_CoversSeasonKeepAndIncompleteBranches()
    {
        var seasonRule = Rule(MediaItemKind.Episode, CleanupRuleTriggerKind.Played, 10) with
        {
            Filters = Filters(MediaItemKind.Episode) with { DeleteEpisodes = SeriesDeleteKind.Season }
        };
        var e1 = Episode("e1", "s1", "show1", Playback("u1", Now.AddDays(-20), true)) with { SeasonEpisodeIds = ["e1", "e2"], FirstSeasonId = "s1", LastSeasonId = "s2" };
        var e2 = Episode("e2", "s1", "show1", Playback("u1", Now.AddDays(-20), true)) with { SeasonEpisodeIds = ["e1", "e2"], FirstSeasonId = "s1", LastSeasonId = "s2" };
        var incomplete = Planner().Plan(new CleanupRequest(Policy(seasonRule), [User("u1")], [e1], false));
        var keepFirst = seasonRule with { Filters = seasonRule.Filters with { KeepSeriesKind = SeriesKeepKind.First } };
        var keepLast = seasonRule with { Filters = seasonRule.Filters with { KeepSeriesKind = SeriesKeepKind.Last } };
        var last1 = e1 with { SeasonId = "s2", FirstSeasonId = "s1", LastSeasonId = "s2" };
        var last2 = e2 with { SeasonId = "s2", FirstSeasonId = "s1", LastSeasonId = "s2" };
        var missingSeason = e1 with { SeasonId = null };

        incomplete.Decisions.Should().BeEmpty();
        Planner().Plan(new CleanupRequest(Policy(keepFirst), [User("u1")], [e1, e2], false)).Decisions.Should().BeEmpty();
        Planner().Plan(new CleanupRequest(Policy(keepLast), [User("u1")], [last1, last2], false)).Decisions.Should().BeEmpty();
        Planner().Plan(new CleanupRequest(Policy(seasonRule), [User("u1")], [missingSeason], false)).Decisions.Should().BeEmpty();
    }

    [Fact]
    public void Plan_CoversSeriesAndSeriesEndedBranches()
    {
        var seriesRule = Rule(MediaItemKind.Episode, CleanupRuleTriggerKind.Played, 10) with
        {
            Filters = Filters(MediaItemKind.Episode) with { DeleteEpisodes = SeriesDeleteKind.Series }
        };
        var endedRule = seriesRule with { Filters = seriesRule.Filters with { DeleteEpisodes = SeriesDeleteKind.SeriesEnded } };
        var e1 = Episode("e1", "s1", "show1", Playback("u1", Now.AddDays(-20), true)) with { SeriesEpisodeIds = ["e1", "e2"] };
        var e2 = Episode("e2", "s1", "show1", Playback("u1", Now.AddDays(-20), true)) with { SeriesEpisodeIds = ["e1", "e2"] };
        var continuing = e1 with { SeriesStatus = MediaSeriesStatus.Continuing, SeriesEpisodeIds = ["e1"] };
        var ended = e1 with { SeriesStatus = MediaSeriesStatus.Ended, SeriesEpisodeIds = ["e1"] };
        var unknown = e1 with { SeriesStatus = MediaSeriesStatus.Unknown, SeriesEpisodeIds = ["e1"] };
        var noSeries = e1 with { SeriesId = null };

        Planner().Plan(new CleanupRequest(Policy(seriesRule), [User("u1")], [e1, e2], false)).Decisions.Should().ContainSingle(x => x.Item.Kind == MediaItemKind.Series);
        Planner().Plan(new CleanupRequest(Policy(seriesRule), [User("u1")], [noSeries], false)).Decisions.Should().BeEmpty();
        Planner().Plan(new CleanupRequest(Policy(endedRule), [User("u1")], [continuing], false)).Decisions.Should().BeEmpty();
        Planner().Plan(new CleanupRequest(Policy(endedRule), [User("u1")], [ended], false)).Decisions.Should().ContainSingle();
        Planner().Plan(new CleanupRequest(Policy(endedRule), [User("u1")], [unknown], false)).Decisions.Should().ContainSingle();
    }

    [Fact]
    public void Plan_CoversEpisodeKeepLastAndNotificationTitleBranches()
    {
        var rule = Rule(MediaItemKind.Episode, CleanupRuleTriggerKind.Played, 10) with
        {
            Filters = Filters(MediaItemKind.Episode) with { DeleteEpisodes = SeriesDeleteKind.Episode, KeepSeriesKind = SeriesKeepKind.Last }
        };
        var first = Episode("e1", "s1", "show1", Playback("u1", Now.AddDays(-20), true)) with { LastEpisodeId = "e2" };
        var last = Episode("e2", "s1", "show1", Playback("u1", Now.AddDays(-20), true)) with { LastEpisodeId = "e2" };

        var plan = Planner().Plan(new CleanupRequest(Policy(rule), [User("u1")], [first, last], false));

        plan.Decisions.Should().ContainSingle(x => x.Item.Id == "e1");
        plan.Decisions[0].Notification.Title.Should().Contain("S01E01");
    }

    [Fact]
    public void Plan_CoversInvalidUserTriggerAndExpiredKindBranches()
    {
        var invalidUsers = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.Played, 10) with
        {
            Filters = Filters(MediaItemKind.Movie) with { UserIds = ["u1"], UsersMode = (UsersListMode)999 }
        };
        var invalidTrigger = Rule(MediaItemKind.Movie, (CleanupRuleTriggerKind)999, 10);
        var disabledByDays = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.Played, -1);
        var movie = Movie("m1", Playback("u1", Now.AddDays(-20), true));

        Planner().Plan(new CleanupRequest(Policy(disabledByDays), [User("u1")], [movie], false)).Decisions.Should().BeEmpty();
        FluentActions.Invoking(() => Planner().Plan(new CleanupRequest(Policy(invalidUsers), [User("u1")], [movie], false))).Should().Throw<NotSupportedException>();
        FluentActions.Invoking(() => Planner().Plan(new CleanupRequest(Policy(invalidTrigger), [User("u1")], [movie], false))).Should().Throw<NotSupportedException>();
    }


    [Fact]
    public void Plan_CoversPlayedCutoffAndAddedAgeSelectedUsers()
    {
        var playedCutoff = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.Played, 10) with
        {
            Trigger = new(CleanupRuleTriggerKind.Played, 10, CountAsNotPlayedAfter: 30)
        };
        var addedAge = Rule(MediaItemKind.Video, CleanupRuleTriggerKind.AddedAge, 10) with
        {
            Filters = Filters(MediaItemKind.Video) with { UserIds = ["u1"], UsersMode = UsersListMode.Acknowledge }
        };
        var beforeStart = Movie("m1", Playback("u1", Now.AddDays(-40), true));
        var afterStart = Movie("m2", Playback("u1", Now.AddDays(-20), true));
        var video = Movie("v1", Playback("u1", Now.AddDays(-1), false), Playback("u2", Now.AddDays(-1), false)) with { Kind = MediaItemKind.Video };

        var plan = Planner().Plan(new CleanupRequest(Policy(playedCutoff, addedAge), [User("u1"), User("u2")], [beforeStart, afterStart, video], false));

        plan.Decisions.Should().Contain(x => x.Item.Id == "m2");
        plan.Decisions.Should().Contain(x => x.Item.Id == "v1" && x.Playback.Select(y => y.UserId).SequenceEqual(new[] { "u1" }));
        plan.Decisions.Should().NotContain(x => x.Item.Id == "m1");
    }

    [Fact]
    public void Plan_CoversPlayedExpirationFalseBranches()
    {
        var anyUser = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.Played, 10);
        var allUsers = anyUser with { Trigger = new(CleanupRuleTriggerKind.Played, 10, PlayedKeepKind.AllUsers) };
        var notExpired = Movie("m1", Playback("u1", Now.AddDays(-1), true));
        var oneExpired = Movie("m2", Playback("u1", Now.AddDays(-20), true), Playback("u2", Now.AddDays(-1), true));
        var bothExpired = Movie("m3", Playback("u1", Now.AddDays(-20), true), Playback("u2", Now.AddDays(-20), true));

        Planner().Plan(new CleanupRequest(Policy(anyUser), [User("u1")], [notExpired], false)).Decisions.Should().BeEmpty();
        Planner().Plan(new CleanupRequest(Policy(allUsers), [User("u1"), User("u2")], [oneExpired], false)).Decisions.Should().BeEmpty();
        Planner().Plan(new CleanupRequest(Policy(allUsers), [User("u1"), User("u2")], [bothExpired], false)).Decisions.Should().ContainSingle();
    }

    [Fact]
    public void Plan_CoversNotPlayedStartDateBranches()
    {
        var noCutoff = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.NotPlayed, 10);
        var withCutoff = noCutoff with { Trigger = new(CleanupRuleTriggerKind.NotPlayed, 10, CountAsNotPlayedAfter: 30) };
        var watched = Movie("m1", Playback("u1", Now.AddDays(-1), false, isWatching: true));
        var oldPlayed = Movie("m2", Playback("u1", Now.AddDays(-40), true));
        var recentPlayed = Movie("m3", Playback("u1", Now.AddDays(-1), true));

        Planner().Plan(new CleanupRequest(Policy(noCutoff), [User("u1")], [watched], false)).Decisions.Should().BeEmpty();
        Planner().Plan(new CleanupRequest(Policy(withCutoff), [User("u1")], [oldPlayed, recentPlayed], false)).Decisions.Should().ContainSingle(x => x.Item.Id == "m2");
    }

    [Fact]
    public void Plan_CoversDeletionCascadeBranches()
    {
        var rule = Rule(MediaItemKind.Episode, CleanupRuleTriggerKind.Played, 10) with
        {
            Filters = Filters(MediaItemKind.Episode) with { DeleteEpisodes = SeriesDeleteKind.Episode }
        };
        var episode = Episode("e1", "s1", "show1", Playback("u1", Now.AddDays(-20), true));
        var season = new MediaItem("s1", MediaItemKind.Season, "s1", "s1", Now.AddDays(-30), "/media/s1", "/media/s1", [], [], "show1", "s1", "show1", "s1", EpisodeIds: ["e1"]);
        var series = new MediaItem("show1", MediaItemKind.Series, "show1", "show1", Now.AddDays(-30), "/media/show1", "/media/show1", [], [], "show1", null, "show1", null, EpisodeIds: ["e1"]);

        var plan = Planner().Plan(new CleanupRequest(Policy(rule), [User("u1")], [episode, season, series], false));

        plan.Deletions.Select(x => x.ItemId).Should().ContainInOrder("e1", "s1", "show1");
    }

    [Fact]
    public void Plan_CoversSeriesFallbackEpisodeIdsBranches()
    {
        var seriesRule = Rule(MediaItemKind.Episode, CleanupRuleTriggerKind.Played, 10) with
        {
            Filters = Filters(MediaItemKind.Episode) with { DeleteEpisodes = SeriesDeleteKind.Series }
        };
        var seasonRule = seriesRule with { Filters = seriesRule.Filters with { DeleteEpisodes = SeriesDeleteKind.Season } };
        var seriesFallback = Episode("e1", "s1", "show1", Playback("u1", Now.AddDays(-20), true)) with { EpisodeIds = ["e1"], SeriesEpisodeIds = null };
        var seasonFallback = seriesFallback with { SeasonEpisodeIds = null };
        var emptySeries = seriesFallback with { EpisodeIds = [], SeriesEpisodeIds = null };

        Planner().Plan(new CleanupRequest(Policy(seriesRule), [User("u1")], [seriesFallback], false)).Decisions.Should().ContainSingle(x => x.Item.Kind == MediaItemKind.Series);
        Planner().Plan(new CleanupRequest(Policy(seasonRule), [User("u1")], [seasonFallback], false)).Decisions.Should().ContainSingle(x => x.Item.Kind == MediaItemKind.Season);
        Planner().Plan(new CleanupRequest(Policy(seriesRule), [User("u1")], [emptySeries], false)).Decisions.Should().BeEmpty();
    }

    [Fact]
    public void Plan_CoversWatchingOnlyPlayedExpirationBranches()
    {
        var anyUser = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.Played, 10);
        var allUsers = anyUser with { Trigger = new(CleanupRuleTriggerKind.Played, 10, PlayedKeepKind.AllUsers) };
        var watchingOnly = Movie("m1", Playback("u1", Now.AddDays(-20), false, isWatching: true));
        var mixed = Movie("m2", Playback("u1", Now.AddDays(-20), true), Playback("u2", Now.AddDays(-20), false, isWatching: true));

        Planner().Plan(new CleanupRequest(Policy(anyUser), [User("u1")], [watchingOnly], false)).Decisions.Should().BeEmpty();
        Planner().Plan(new CleanupRequest(Policy(allUsers), [User("u1"), User("u2")], [mixed], false)).Decisions.Should().BeEmpty();
    }

    [Fact]
    public void Plan_CoversFavoriteFiltersWhenNoFavoriteUsersAreSelected()
    {
        var favoriteAll = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.AddedAge, 10) with
        {
            Filters = Filters(MediaItemKind.Movie) with { FavoriteFilter = RuleFavoriteFilterKind.FavoriteByAllUsers }
        };
        var notFavoriteAll = favoriteAll with
        {
            Filters = favoriteAll.Filters with { FavoriteFilter = RuleFavoriteFilterKind.NotFavoriteByAllUsers }
        };
        var movie = Movie("m1");

        Planner().Plan(new CleanupRequest(Policy(favoriteAll), [], [movie], false)).Decisions.Should().BeEmpty();
        Planner().Plan(new CleanupRequest(Policy(notFavoriteAll), [], [movie], false)).Decisions.Should().BeEmpty();
    }

    [Fact]
    public void Plan_CoversNullDeletionCollectionsAndFallbackNames()
    {
        var episodeRule = Rule(MediaItemKind.Episode, CleanupRuleTriggerKind.Played, 10) with
        {
            Filters = Filters(MediaItemKind.Episode) with { DeleteEpisodes = SeriesDeleteKind.Episode }
        };
        var seasonRule = episodeRule with { Filters = episodeRule.Filters with { DeleteEpisodes = SeriesDeleteKind.Season } };
        var seriesRule = episodeRule with { Filters = episodeRule.Filters with { DeleteEpisodes = SeriesDeleteKind.Series } };
        var episode = Episode("e1", "s1", "show1", Playback("u1", Now.AddDays(-20), true)) with { SeasonName = null, SeriesName = null, SeasonEpisodeIds = ["e1"], SeriesEpisodeIds = ["e1"] };
        var seasonWithoutEpisodes = new MediaItem("s1", MediaItemKind.Season, "s1", "s1", Now.AddDays(-30), "/media/s1", "/media/s1", [], [], "show1", "s1", "show1", "s1");
        var seriesWithoutEpisodes = new MediaItem("show1", MediaItemKind.Series, "show1", "show1", Now.AddDays(-30), "/media/show1", "/media/show1", [], [], "show1", null, "show1", null);

        var cascade = Planner().Plan(new CleanupRequest(Policy(episodeRule), [User("u1")], [episode, seasonWithoutEpisodes, seriesWithoutEpisodes], false));
        cascade.Deletions.Should().ContainSingle(x => x.ItemId == "e1");

        Planner().Plan(new CleanupRequest(Policy(seasonRule), [User("u1")], [episode], false)).Decisions.Should().ContainSingle(x => x.Item.Name == "e1");
        Planner().Plan(new CleanupRequest(Policy(seriesRule), [User("u1")], [episode], false)).Decisions.Should().ContainSingle(x => x.Item.Name == "e1");
    }

    [Fact]
    public void InternalDefensiveBranches_AreCoveredDirectly()
    {
        FluentActions.Invoking(() => CleanupRuleKinds.ToExpiredKind((CleanupRuleTriggerKind)999))
            .Should().Throw<NotSupportedException>();
        FluentActions.Invoking(() => CleanupRuleKinds.Priority((ExpiredKind)999))
            .Should().Throw<NotSupportedException>();

        var movie = Movie("m1");
        var played = CleanupDecisionFactory.Create(movie, ExpiredKind.Played, [], [], ["direct"]);
        played.Notification.ShortOverview.Should().Be("Played item expired");

        FluentActions.Invoking(() => CleanupDecisionFactory.Create(movie, (ExpiredKind)999, [], [], []))
            .Should().Throw<NotSupportedException>();
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

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private sealed class TestPathMatcher : IPathMatcher
    {
        public bool ContainsSubPath(string parentPath, string path) =>
            path.StartsWith(parentPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }
}
