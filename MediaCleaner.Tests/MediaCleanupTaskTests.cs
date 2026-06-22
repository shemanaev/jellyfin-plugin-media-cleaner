using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Globalization;
using FluentAssertions;
using MediaCleaner.Adapters;
using MediaCleaner.Core;
using Microsoft.Extensions.Logging;
using Moq;

#if JELLYFIN_USER_IN_DATA_ENTITIES
using JellyfinUser = Jellyfin.Data.Entities.User;
#else
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;
#endif

namespace MediaCleaner.Tests;

public class MediaCleanupTaskTests
{
    [Fact]
    public async Task ExecuteAsync_SkipsMutation_WhenMigratedRulesNeedReview()
    {
        var mutation = new RecordingMutationAdapter();
        var task = CreateTask(requiresMigrationReview: true, mutation);

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        mutation.ExecuteCount.Should().Be(0);
        task.LastPlan.Should().NotBeNull();
        task.LastPlan!.Decisions.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_RunsMutation_WhenRulesAreReviewed()
    {
        var mutation = new RecordingMutationAdapter();
        var task = CreateTask(requiresMigrationReview: false, mutation);

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        mutation.ExecuteCount.Should().Be(1);
    }

    [Fact]
    public void NotificationOverview_IncludesPathAndItemDecisionLog()
    {
        var item = CreateItem();
        var plan = new CleanupPlanner(new FixedClock(), new OrdinalPathMatcher(), new NoExtraFileProbe())
            .Plan(new CleanupRequest(CreatePolicy(), [new MediaUser("user", "User")], [item], false));

        var notificationDecision = JellyfinMutationAdapter.WithNotificationOverview(plan.Decisions.Single(), plan.AuditEntries);
        var overview = notificationDecision.Notification.Overview;

        overview.Should().Contain("Path:");
        overview.Should().Contain(@"C:\Media\Movie.mkv");
        overview.Should().Contain("Decision log:");
        overview.Should().Contain("Trigger -> Matched [rule] (Delete):");
        overview.Should().Contain("DeletionCascade -> Planned (Delete): planned deletion");
    }

    [Fact]
    public void CleanupAuditFormatter_FormatsSharedPlainTextDecisionLog()
    {
        var entry = new CleanupAuditEntry(
            "item",
            "Movie",
            MediaItemKind.Movie,
            "rule",
            "cleanup rule",
            CleanupRuleActionKind.Delete,
            CleanupAuditStage.Trigger,
            CleanupAuditOutcome.Matched,
            "trigger matched");
        var blocked = entry with { Outcome = CleanupAuditOutcome.Blocked };

        CleanupAuditFormatter.GetItemEntries([entry, entry with { ItemId = "other" }], "ITEM").Should().ContainSingle();
        CleanupAuditFormatter.FormatPlainTextEntry(entry).Should().Be("Trigger -> Matched [cleanup rule]: trigger matched");
        CleanupAuditFormatter.FormatPlainTextEntry(entry, includeAction: true).Should().Be("Trigger -> Matched [cleanup rule] (Delete): trigger matched");
        CleanupAuditFormatter.GetFinalOutcome([entry, blocked]).Should().Be(CleanupAuditOutcome.Blocked);
    }

    [Theory]
    [InlineData(CleanupAuditOutcome.Suppressed)]
    [InlineData(CleanupAuditOutcome.Planned)]
    [InlineData(CleanupAuditOutcome.Protected)]
    [InlineData(CleanupAuditOutcome.Rejected)]
    [InlineData(CleanupAuditOutcome.Skipped)]
    [InlineData(CleanupAuditOutcome.Matched)]
    public void CleanupAuditFormatter_SelectsFinalOutcomeByPriority(CleanupAuditOutcome finalOutcome)
    {
        var entries = new[]
        {
            new CleanupAuditEntry(
                "item",
                "Movie",
                MediaItemKind.Movie,
                "rule",
                "cleanup rule",
                CleanupRuleActionKind.Delete,
                CleanupAuditStage.Trigger,
                CleanupAuditOutcome.Matched,
                "trigger matched"),
            new CleanupAuditEntry(
                "item",
                "Movie",
                MediaItemKind.Movie,
                "rule",
                "cleanup rule",
                CleanupRuleActionKind.Delete,
                CleanupAuditStage.Trigger,
                finalOutcome,
                "final outcome"),
        };

        CleanupAuditFormatter.GetFinalOutcome(entries).Should().Be(finalOutcome);
    }

    [Fact]
    public void CleanupAudit_UsesItemName_WhenFullNameIsMissing()
    {
        var item = CreateItem() with { Name = "Fallback name", FullName = " " };

        CleanupAudit.GetItemDisplayName(item).Should().Be("Fallback name");
    }

    private static MediaCleanupTask CreateTask(bool requiresMigrationReview, RecordingMutationAdapter mutation) =>
        new(
            Mock.Of<ILogger<MediaCleanupTask>>(),
            Mock.Of<ILocalizationManager>(),
            new TestPolicyProvider(CreatePolicy(), requiresMigrationReview),
            new TestCatalogAdapter(CreateItem()),
            new CleanupPlanner(new FixedClock(), new OrdinalPathMatcher(), new NoExtraFileProbe()),
            mutation);

    private static CleanupPolicy CreatePolicy() =>
        new(
            [
                new CleanupRule(
                    "rule",
                    "rule",
                    Enabled: true,
                    Trigger: new CleanupRuleTrigger(CleanupRuleTriggerKind.Played, Days: 1),
                    Filters: new CleanupRuleFilters(
                        MediaKinds: [MediaItemKind.Movie],
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
                        KeepSeriesKind: SeriesKeepKind.None),
                    Actions: new CleanupRuleActions(CleanupRuleActionKind.Delete, MarkAsUnplayed: false)),
            ],
            AllowDeleteIfPlayedBeforeAdded: false);

    private static MediaItem CreateItem() =>
        new(
            Id: "item",
            Kind: MediaItemKind.Movie,
            Name: "Movie",
            FullName: "Movie",
            DateCreated: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Path: @"C:\Media\Movie.mkv",
            LocationPath: @"C:\Media",
            Tags: [],
            Playback:
            [
                new PlaybackState(
                    UserId: "user",
                    LastPlayedDate: new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                    IsPlayed: true,
                    IsWatching: false,
                    IsFavorite: false,
                    UserName: "User"),
            ]);

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    private sealed class TestPolicyProvider(CleanupPolicy policy, bool requiresMigrationReview) : ICleanupPolicyProvider
    {
        public CleanupPolicy GetPolicy() => policy;

        public bool RequiresMigrationReview { get; } = requiresMigrationReview;
    }

    private sealed class TestCatalogAdapter(MediaItem item) : IMediaCatalogAdapter
    {
        public CleanupCatalog Create(CleanupPolicy policy, CancellationToken cancellationToken) =>
            new(
                [new MediaUser("user", "User")],
                [item],
                new Dictionary<string, BaseItem>(),
                new Dictionary<string, JellyfinUser>());
    }

    private sealed class RecordingMutationAdapter : IMediaMutationAdapter
    {
        public int ExecuteCount { get; private set; }

        public Task ExecuteAsync(CleanupPlan plan, CleanupCatalog catalog, CancellationToken cancellationToken)
        {
            ExecuteCount++;
            return Task.CompletedTask;
        }
    }
}
