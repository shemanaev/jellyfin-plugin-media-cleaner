using FluentAssertions;
using MediaCleaner.Core;

namespace MediaCleaner.Tests;

public class CleanupPlannerTests
{
    private static readonly DateTime Now = new(2026, 06, 22, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FilterUsers_AppliesIgnoreAndAcknowledgeModes()
    {
        var users = new[] { new MediaUser("u1", "one"), new MediaUser("u2", "two") };

        CleanupPlanner.FilterUsers(users, ["u1"], UsersListMode.Ignore).Should().ContainSingle(x => x.Id == "u2");
        CleanupPlanner.FilterUsers(users, ["u1"], UsersListMode.Acknowledge).Should().ContainSingle(x => x.Id == "u1");
    }

    [Fact]
    public void Plan_ReturnsEmpty_WhenNoEnabledRulesExist()
    {
        var request = new CleanupRequest(new CleanupPolicy([], false), [User("u1")], [Movie("m1")], false);

        Planner().Plan(request).Should().Be(CleanupPlan.Empty);
    }

    [Theory]
    [InlineData(PlayedKeepKind.AnyUser)]
    [InlineData(PlayedKeepKind.AnyUserRolling)]
    [InlineData(PlayedKeepKind.AllUsers)]
    public void Plan_DeletesPlayedItems_WhenConfiguredPlayedRuleIsExpired(PlayedKeepKind keepKind)
    {
        var rule = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.Played, 10) with
        {
            Trigger = new(CleanupRuleTriggerKind.Played, 10, keepKind),
            Actions = new(CleanupRuleActionKind.Delete, MarkAsUnplayed: true),
        };
        var movie = Movie("m1", Playback("u1", Now.AddDays(-12), isPlayed: true));

        var plan = Planner().Plan(new CleanupRequest(Policy(rule), [User("u1")], [movie], false));

        plan.Decisions.Should().ContainSingle(x => x.Kind == ExpiredKind.Played);
        plan.Decisions[0].MarkUnplayedUserIds.Should().Equal("u1");
        plan.Decisions[0].MatchedRules.Should().Equal(rule.Name);
        plan.Deletions.Should().ContainSingle(x => x.ItemId == "m1");
        plan.AuditEntries.Should().Contain(x => x.ItemId == "m1" && x.Stage == CleanupAuditStage.Trigger && x.Outcome == CleanupAuditOutcome.Matched);
        plan.AuditEntries.Should().Contain(x => x.ItemId == "m1" && x.Stage == CleanupAuditStage.DeletionCascade && x.Outcome == CleanupAuditOutcome.Planned);
    }

    [Fact]
    public void Plan_DoesNotDeleteRollingPlayedItem_WhenAnyUserIsWatching()
    {
        var rule = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.Played, 10) with
        {
            Trigger = new(CleanupRuleTriggerKind.Played, 10, PlayedKeepKind.AnyUserRolling)
        };
        var movie = Movie("m1",
            Playback("u1", Now.AddDays(-12), isPlayed: true),
            Playback("u2", Now.AddDays(-1), isPlayed: false, isWatching: true));

        Planner().Plan(new CleanupRequest(Policy(rule), [User("u1"), User("u2")], [movie], false)).Decisions.Should().BeEmpty();
    }

    [Fact]
    public void Plan_DeletesNotPlayedAndAddedAgeItems_WhenTriggersMatch()
    {
        var notPlayed = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.NotPlayed, 10);
        var addedAge = Rule(MediaItemKind.Video, CleanupRuleTriggerKind.AddedAge, 10);
        var movie = Movie("m1", Playback("u1", null, isPlayed: false));
        var video = Movie("v1") with { Kind = MediaItemKind.Video };

        var plan = Planner().Plan(new CleanupRequest(Policy(notPlayed, addedAge), [User("u1")], [movie, video], false));

        plan.Decisions.Should().Contain(x => x.Item.Id == "m1" && x.Kind == ExpiredKind.NotPlayed);
        plan.Decisions.Should().Contain(x => x.Item.Id == "v1" && x.Kind == ExpiredKind.AddedAge);
    }

    [Fact]
    public void Plan_RespectsPlayedBeforeAddedPolicy()
    {
        var rule = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.Played, 10);
        var movie = Movie("m1", Playback("u1", Now.AddDays(-12), isPlayed: true)) with { DateCreated = Now.AddDays(-1) };
        var request = new CleanupRequest(Policy(rule), [User("u1")], [movie], false);

        Planner().Plan(request).Decisions.Should().BeEmpty();

        Planner().Plan(request with { Policy = Policy(true, rule) }).Decisions.Should().ContainSingle();
    }

    [Fact]
    public void Plan_AuditsPlayedBeforeAddedPlayback_WhenPlayedRuleSkipsIt()
    {
        var rule = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.Played, 10);
        var movie = Movie("m1", Playback("u1", Now.AddDays(-12), isPlayed: true)) with { DateCreated = Now.AddDays(-1) };

        var plan = Planner().Plan(new CleanupRequest(Policy(rule), [User("u1")], [movie], false));

        plan.Decisions.Should().BeEmpty();
        var audit = plan.AuditEntries.Should().ContainSingle(x =>
            x.ItemId == "m1" &&
            x.Stage == CleanupAuditStage.Trigger &&
            x.Outcome == CleanupAuditOutcome.Skipped).Subject;
        audit.Reason.Should().Contain("Last Played");
        audit.Reason.Should().Contain("before Date Added");
        audit.Reason.Should().Contain("file upgrade or re-import");
    }

    [Fact]
    public void Plan_BlocksNotPlayedMatch_WhenPlaybackExistsBeforeAddedDate()
    {
        var rule = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.NotPlayed, 10);
        var movie = Movie("m1", Playback("u1", Now.AddDays(-40), isPlayed: true)) with { DateCreated = Now.AddDays(-30) };

        var plan = Planner().Plan(new CleanupRequest(Policy(rule), [User("u1")], [movie], false));

        plan.Decisions.Should().BeEmpty();
        plan.Deletions.Should().BeEmpty();
        plan.AuditEntries.Should().Contain(x =>
            x.ItemId == "m1" &&
            x.Stage == CleanupAuditStage.Trigger &&
            x.Outcome == CleanupAuditOutcome.Skipped &&
            x.Reason.Contains("blocked not-played match") &&
            x.Reason.Contains("before Date Added"));
    }

    [Fact]
    public void Plan_BlocksNotPlayedMatch_WhenPlaybackBeforeAddedIsInsideHistoryWindow()
    {
        var rule = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.NotPlayed, 10) with
        {
            Trigger = new(CleanupRuleTriggerKind.NotPlayed, 10, CountAsNotPlayedAfter: 45)
        };
        var movie = Movie("m1", Playback("u1", Now.AddDays(-40), isPlayed: true)) with { DateCreated = Now.AddDays(-30) };

        var plan = Planner().Plan(new CleanupRequest(Policy(rule), [User("u1")], [movie], false));

        plan.Decisions.Should().BeEmpty();
        plan.AuditEntries.Should().Contain(x =>
            x.ItemId == "m1" &&
            x.Stage == CleanupAuditStage.Trigger &&
            x.Outcome == CleanupAuditOutcome.Skipped &&
            x.Reason.Contains("blocked not-played match"));
    }

    [Fact]
    public void Plan_UsesNotPlayedCutoffFromTrigger()
    {
        var rule = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.NotPlayed, 10) with
        {
            Trigger = new(CleanupRuleTriggerKind.NotPlayed, 10, CountAsNotPlayedAfter: 30)
        };
        var beforeStart = Movie("m1", Playback("u1", Now.AddDays(-40), isPlayed: true));
        var afterStart = Movie("m2", Playback("u1", Now.AddDays(-1), isPlayed: true));

        var plan = Planner().Plan(new CleanupRequest(Policy(rule), [User("u1")], [beforeStart, afterStart], false));

        plan.Decisions.Should().ContainSingle(x => x.Item.Id == "m1");
    }

    [Fact]
    public void Plan_RespectsFavoriteLocationAndTagFilters()
    {
        var favoriteRule = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.Played, 10) with
        {
            Filters = Filters(MediaItemKind.Movie) with { FavoriteFilter = RuleFavoriteFilterKind.NotFavoriteByAnyUser }
        };
        var locationRule = Rule(MediaItemKind.Video, CleanupRuleTriggerKind.Played, 10) with
        {
            Filters = Filters(MediaItemKind.Video) with { Locations = [@"D:\media"], LocationsMode = LocationsListMode.Include }
        };
        var tagRule = Rule(MediaItemKind.Audio, CleanupRuleTriggerKind.Played, 10) with
        {
            Filters = Filters(MediaItemKind.Audio) with { EnableTagFilter = true, TagFilterMode = TagMode.Inclusion, Tags = ["delete"] }
        };
        var favorite = Movie("m1", Playback("u1", Now.AddDays(-20), true, isFavorite: true));
        var outside = Movie("v1", Playback("u1", Now.AddDays(-20), true)) with { Kind = MediaItemKind.Video, LocationPath = @"E:\media\v1.mkv" };
        var tagged = Movie("a1", Playback("u1", Now.AddDays(-20), true)) with { Kind = MediaItemKind.Audio, Tags = ["delete"] };

        var plan = Planner().Plan(new CleanupRequest(Policy(favoriteRule, locationRule, tagRule), [User("u1")], [favorite, outside, tagged], false));

        plan.Decisions.Should().ContainSingle(x => x.Item.Id == "a1");
        plan.AuditEntries.Should().Contain(x => x.ItemId == "m1" && x.Stage == CleanupAuditStage.FavoriteFilter && x.Outcome == CleanupAuditOutcome.Rejected);
        plan.AuditEntries.Should().Contain(x => x.ItemId == "v1" && x.Stage == CleanupAuditStage.LocationFilter && x.Outcome == CleanupAuditOutcome.Rejected);
        plan.AuditEntries.Should().Contain(x => x.ItemId == "a1" && x.Stage == CleanupAuditStage.DeletionCascade && x.Outcome == CleanupAuditOutcome.Planned);
    }

    [Fact]
    public void Plan_AppliesSeriesPolicyAndKeepFirstEpisode()
    {
        var rule = Rule(MediaItemKind.Episode, CleanupRuleTriggerKind.Played, 10) with
        {
            Filters = Filters(MediaItemKind.Episode) with { DeleteEpisodes = SeriesDeleteKind.Episode, KeepSeriesKind = SeriesKeepKind.First }
        };
        var first = Episode("e1", "s1", "show1", Playback("u1", Now.AddDays(-20), true)) with { FirstEpisodeId = "e1" };
        var second = Episode("e2", "s1", "show1", Playback("u1", Now.AddDays(-20), true)) with
        {
            FirstEpisodeId = "e1",
            FullName = "The Show | S01E02 | The Episode"
        };

        var plan = Planner().Plan(new CleanupRequest(Policy(rule), [User("u1")], [first, second], false));

        plan.Decisions.Should().ContainSingle(x => x.Item.Id == "e2");
        plan.AuditEntries.Should().Contain(x => x.ItemId == "e1" && x.Stage == CleanupAuditStage.SeriesPolicy && x.Outcome == CleanupAuditOutcome.Rejected);
        plan.AuditEntries.Should().Contain(x => x.ItemId == "e2" && x.ItemName == "The Show | S01E02 | The Episode");
    }

    [Fact]
    public void Plan_ConvertsFullyExpiredSeason_WhenSeasonDeleteModeIsUsed()
    {
        var rule = Rule(MediaItemKind.Episode, CleanupRuleTriggerKind.Played, 10) with
        {
            Filters = Filters(MediaItemKind.Episode) with { DeleteEpisodes = SeriesDeleteKind.Season }
        };
        var e1 = Episode("e1", "s1", "show1", Playback("u1", Now.AddDays(-20), true)) with { SeasonEpisodeIds = ["e1", "e2"], SeriesEpisodeIds = ["e1", "e2"] };
        var e2 = Episode("e2", "s1", "show1", Playback("u1", Now.AddDays(-20), true)) with { SeasonEpisodeIds = ["e1", "e2"], SeriesEpisodeIds = ["e1", "e2"] };
        var season = Season("s1", "show1", ["e1", "e2"]);
        var series = Series("show1", ["e1", "e2"]);

        var plan = Planner().Plan(new CleanupRequest(Policy(rule), [User("u1")], [e1, e2, season, series], false));

        plan.Decisions.Should().ContainSingle(x => x.Item.Kind == MediaItemKind.Season);
        plan.Deletions.Select(x => x.ItemId).Should().ContainInOrder("e1", "e2", "s1", "show1");
        plan.AuditEntries.Should().Contain(x => x.ItemId == "s1" && x.Stage == CleanupAuditStage.SeriesPolicy && x.Outcome == CleanupAuditOutcome.Matched);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Plan_ProtectRuleSuppressesDeleteRegardlessOfRuleOrderAndWritesAudit(bool protectFirst)
    {
        var deleteRule = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.Played, 10) with { Id = "delete", Name = "delete old" };
        var protectRule = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.Played, 10) with
        {
            Id = "protect",
            Name = "protect old",
            Actions = new(CleanupRuleActionKind.Protect, false),
        };
        var rules = protectFirst ? new[] { protectRule, deleteRule } : new[] { deleteRule, protectRule };
        var movie = Movie("m1", Playback("u1", Now.AddDays(-20), true));

        var plan = Planner().Plan(new CleanupRequest(Policy(rules), [User("u1")], [movie], false));

        plan.Decisions.Should().BeEmpty();
        plan.Deletions.Should().BeEmpty();
        plan.AuditEntries.Should().Contain(x => x.ItemId == "m1" && x.Stage == CleanupAuditStage.Protection && x.Outcome == CleanupAuditOutcome.Protected);
        plan.AuditEntries.Should().Contain(x => x.ItemId == "m1" && x.Stage == CleanupAuditStage.Protection && x.Outcome == CleanupAuditOutcome.Suppressed);
    }

    [Fact]
    public void Plan_ProtectedEpisodeBlocksSeasonCascade()
    {
        var deleteRule = Rule(MediaItemKind.Episode, CleanupRuleTriggerKind.Played, 10) with
        {
            Filters = Filters(MediaItemKind.Episode) with { DeleteEpisodes = SeriesDeleteKind.Season }
        };
        var protectRule = Rule(MediaItemKind.Episode, CleanupRuleTriggerKind.Played, 10) with
        {
            Id = "protect",
            Name = "protect tagged episode",
            Actions = new(CleanupRuleActionKind.Protect, false),
            Filters = Filters(MediaItemKind.Episode) with
            {
                DeleteEpisodes = SeriesDeleteKind.Episode,
                EnableTagFilter = true,
                TagFilterMode = TagMode.Inclusion,
                Tags = ["keep"]
            }
        };
        var e1 = Episode("e1", "s1", "show1", Playback("u1", Now.AddDays(-20), true)) with { SeasonEpisodeIds = ["e1", "e2"], SeriesEpisodeIds = ["e1", "e2"] };
        var e2 = Episode("e2", "s1", "show1", Playback("u1", Now.AddDays(-20), true)) with { Tags = ["keep"], SeasonEpisodeIds = ["e1", "e2"], SeriesEpisodeIds = ["e1", "e2"] };
        var season = Season("s1", "show1", ["e1", "e2"]);
        var series = Series("show1", ["e1", "e2"]);

        var plan = Planner().Plan(new CleanupRequest(Policy(deleteRule, protectRule), [User("u1")], [e1, e2, season, series], false));

        plan.Decisions.Should().ContainSingle(x => x.Item.Kind == MediaItemKind.Season);
        plan.Deletions.Should().BeEmpty();
        plan.AuditEntries.Should().Contain(x => x.ItemId == "s1" && x.Stage == CleanupAuditStage.DeletionCascade && x.Outcome == CleanupAuditOutcome.Blocked && x.Reason.Contains("contains protected item 'e2'"));
    }

    [Fact]
    public void Plan_ProtectedEpisodeBlocksSeriesCascade()
    {
        var deleteRule = Rule(MediaItemKind.Episode, CleanupRuleTriggerKind.Played, 10) with
        {
            Filters = Filters(MediaItemKind.Episode) with { DeleteEpisodes = SeriesDeleteKind.Series }
        };
        var protectRule = Rule(MediaItemKind.Episode, CleanupRuleTriggerKind.Played, 10) with
        {
            Id = "protect",
            Name = "protect tagged episode",
            Actions = new(CleanupRuleActionKind.Protect, false),
            Filters = Filters(MediaItemKind.Episode) with
            {
                DeleteEpisodes = SeriesDeleteKind.Episode,
                EnableTagFilter = true,
                TagFilterMode = TagMode.Inclusion,
                Tags = ["keep"]
            }
        };
        var e1 = Episode("e1", "s1", "show1", Playback("u1", Now.AddDays(-20), true)) with { SeriesEpisodeIds = ["e1", "e2"] };
        var e2 = Episode("e2", "s1", "show1", Playback("u1", Now.AddDays(-20), true)) with { Tags = ["keep"], SeriesEpisodeIds = ["e1", "e2"] };
        var series = Series("show1", ["e1", "e2"]);

        var plan = Planner().Plan(new CleanupRequest(Policy(deleteRule, protectRule), [User("u1")], [e1, e2, series], false));

        plan.Decisions.Should().ContainSingle(x => x.Item.Kind == MediaItemKind.Series);
        plan.Deletions.Should().BeEmpty();
        plan.AuditEntries.Should().Contain(x => x.ItemId == "show1" && x.Stage == CleanupAuditStage.DeletionCascade && x.Outcome == CleanupAuditOutcome.Blocked && x.Reason.Contains("contains protected item 'e2'"));
    }

    [Fact]
    public void Plan_ProtectedSeasonBlocksSeriesCascade()
    {
        var deleteRule = Rule(MediaItemKind.Episode, CleanupRuleTriggerKind.Played, 10) with
        {
            Filters = Filters(MediaItemKind.Episode) with { DeleteEpisodes = SeriesDeleteKind.Series }
        };
        var protectRule = Rule(MediaItemKind.Season, CleanupRuleTriggerKind.AddedAge, 10) with
        {
            Id = "protect-season",
            Name = "protect season",
            Actions = new(CleanupRuleActionKind.Protect, false),
        };
        var e1 = Episode("e1", "s1", "show1", Playback("u1", Now.AddDays(-20), true)) with { SeriesEpisodeIds = ["e1", "e2"] };
        var e2 = Episode("e2", "s1", "show1", Playback("u1", Now.AddDays(-20), true)) with { SeriesEpisodeIds = ["e1", "e2"] };
        var season = Season("s1", "show1", ["e1", "e2"]);
        var series = Series("show1", ["e1", "e2"]) with { SeasonIds = ["s1"] };

        var plan = Planner().Plan(new CleanupRequest(Policy(deleteRule, protectRule), [User("u1")], [e1, e2, season, series], false));

        plan.Decisions.Should().ContainSingle(x => x.Item.Kind == MediaItemKind.Series);
        plan.Deletions.Should().BeEmpty();
        plan.AuditEntries.Should().Contain(x => x.ItemId == "show1" && x.Stage == CleanupAuditStage.DeletionCascade && x.Outcome == CleanupAuditOutcome.Blocked && x.Reason.Contains("contains protected item 's1'"));
    }

    [Fact]
    public void Plan_ProtectedParentSuppressesOnlyCascadedParentDeletion()
    {
        var deleteRule = Rule(MediaItemKind.Episode, CleanupRuleTriggerKind.Played, 10) with
        {
            Filters = Filters(MediaItemKind.Episode) with { DeleteEpisodes = SeriesDeleteKind.Episode }
        };
        var protectRule = Rule(MediaItemKind.Series, CleanupRuleTriggerKind.AddedAge, 10) with
        {
            Id = "protect-series",
            Name = "protect series",
            Actions = new(CleanupRuleActionKind.Protect, false),
        };
        var e1 = Episode("e1", "s1", "show1", Playback("u1", Now.AddDays(-20), true)) with { SeasonEpisodeIds = ["e1", "e2"], SeriesEpisodeIds = ["e1", "e2"] };
        var e2 = Episode("e2", "s1", "show1", Playback("u1", Now.AddDays(-20), true)) with { SeasonEpisodeIds = ["e1", "e2"], SeriesEpisodeIds = ["e1", "e2"] };
        var season = Season("s1", "show1", ["e1", "e2"]);
        var series = Series("show1", ["e1", "e2"]);

        var plan = Planner().Plan(new CleanupRequest(Policy(deleteRule, protectRule), [User("u1")], [e1, e2, season, series], false));

        plan.Deletions.Select(x => x.ItemId).Should().ContainInOrder("e1", "e2", "s1");
        plan.Deletions.Should().NotContain(x => x.ItemId == "show1");
    }

    [Fact]
    public void Plan_DeduplicatesDeleteDecisions_WhenMultipleRulesMatchSameItem()
    {
        var first = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.Played, 10) with { Id = "r1", Name = "first" };
        var second = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.Played, 15) with { Id = "r2", Name = "second" };
        var movie = Movie("m1", Playback("u1", Now.AddDays(-20), true));

        var plan = Planner().Plan(new CleanupRequest(Policy(first, second), [User("u1")], [movie], false));

        plan.Decisions.Should().ContainSingle();
        plan.Decisions[0].MatchedRules.Should().BeEquivalentTo(["first", "second"]);
        plan.Deletions.Should().ContainSingle(x => x.ItemId == "m1");
    }

    [Fact]
    public void Plan_DeduplicatesWithStableKind_WhenDifferentTriggersMatchSameItem()
    {
        var addedAge = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.AddedAge, 10) with { Id = "r2", Name = "added age" };
        var played = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.Played, 10) with { Id = "r1", Name = "played" };
        var movie = Movie("m1", Playback("u1", Now.AddDays(-20), true));

        var plan = Planner().Plan(new CleanupRequest(Policy(addedAge, played), [User("u1")], [movie], false));

        plan.Decisions.Should().ContainSingle();
        plan.Decisions[0].Kind.Should().Be(ExpiredKind.Played);
        plan.Decisions[0].MatchedRules.Should().Equal("added age", "played");
        plan.Decisions[0].Reason.Should().Contain("expired for");
    }

    [Fact]
    public void Plan_DryRun_ReturnsDecisionsWithoutDeletions()
    {
        var rule = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.Played, 10);
        var request = new CleanupRequest(Policy(rule), [User("u1")], [Movie("m1", Playback("u1", Now.AddDays(-20), true))], true);

        var plan = Planner().Plan(request);

        plan.Decisions.Should().ContainSingle();
        plan.Deletions.Should().BeEmpty();
        plan.AuditEntries.Should().Contain(x => x.ItemId == "m1" && x.Stage == CleanupAuditStage.DeletionCascade && x.Outcome == CleanupAuditOutcome.Planned);
    }

    [Fact]
    public void Plan_WritesRuleLevelAudit_WhenPlayedRuleHasNoMatchedUsers()
    {
        var rule = Rule(MediaItemKind.Movie, CleanupRuleTriggerKind.Played, 10) with
        {
            Filters = Filters(MediaItemKind.Movie) with { UserIds = ["missing"], UsersMode = UsersListMode.Acknowledge }
        };

        var plan = Planner().Plan(new CleanupRequest(Policy(rule), [User("u1")], [Movie("m1", Playback("u1", Now.AddDays(-20), true))], false));

        plan.Decisions.Should().BeEmpty();
        plan.AuditEntries.Should().ContainSingle(x =>
            x.ItemId == null &&
            x.RuleId == rule.Id &&
            x.Stage == CleanupAuditStage.RuleEligibility &&
            x.Outcome == CleanupAuditOutcome.Skipped);
    }

    private static CleanupPlanner Planner(IExtraFileProbe? extraFileProbe = null) =>
        new(new FixedClock(Now), new TestPathMatcher(), extraFileProbe ?? new NoExtraFileProbe());

    private static CleanupPolicy Policy(params CleanupRule[] rules) => new(rules, false);

    private static CleanupPolicy Policy(bool allowDeleteIfPlayedBeforeAdded, params CleanupRule[] rules) => new(rules, allowDeleteIfPlayedBeforeAdded);

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

    private static MediaItem Season(string id, string seriesId, IReadOnlyList<string> episodeIds) =>
        new(id, MediaItemKind.Season, id, id, Now.AddDays(-30), $"/media/{id}", $"/media/{id}", [], [], seriesId, id, seriesId, id, 1, 1, EpisodeIds: episodeIds);

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
}
