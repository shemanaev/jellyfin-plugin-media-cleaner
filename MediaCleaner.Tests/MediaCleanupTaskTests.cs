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
    public async Task ExecuteAsync_SkipsMutationAndDoesNotRetainPlan_WhenMigratedRulesNeedReview()
    {
        var mutation = new RecordingMutationAdapter();
        var task = CreateTask(requiresMigrationReview: true, mutation);

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        mutation.ExecuteCount.Should().Be(0);
        task.LastPlan.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_RunsMutation_WhenRulesAreReviewed()
    {
        var mutation = new RecordingMutationAdapter();
        var task = CreateTask(requiresMigrationReview: false, mutation);

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        mutation.ExecuteCount.Should().Be(1);
        task.LastPlan.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_RetainsPlanOnlyForDryRun()
    {
        var mutation = new RecordingMutationAdapter();
        var task = CreateTask(requiresMigrationReview: false, mutation, isDryRun: true);

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        mutation.ExecuteCount.Should().Be(1);
        task.LastPlan.Should().NotBeNull();
        task.LastPlan!.Decisions.Should().ContainSingle();
        task.LastPlan.AuditEntries.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_DiagnosticRun_LogsPhasesWithOneCorrelationId()
    {
        const string diagnosticRunId = "report-123";
        var logger = new Mock<ILogger<MediaCleanupTask>>();
        var mutation = new RecordingMutationAdapter();
        var task = CreateTask(
            requiresMigrationReview: false,
            mutation,
            isDryRun: true,
            logger,
            diagnosticRunId);

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        var messages = logger.Messages()
            .Where(message => message.Contains("diagnostic run", StringComparison.Ordinal))
            .ToList();
        messages.Should().NotBeEmpty();
        messages.Should().OnlyContain(message => message.Contains(diagnosticRunId, StringComparison.Ordinal));
        messages.Should().Contain(message => message.Contains("policy loading started", StringComparison.Ordinal));
        messages.Should().Contain(message => message.Contains("policy loading completed", StringComparison.Ordinal));
        messages.Should().Contain(message => message.Contains("catalog snapshot started", StringComparison.Ordinal));
        messages.Should().Contain(message => message.Contains("catalog snapshot completed", StringComparison.Ordinal));
        messages.Should().Contain(message => message.Contains("planner started", StringComparison.Ordinal));
        messages.Should().Contain(message => message.Contains("planner completed", StringComparison.Ordinal));
        messages.Should().Contain(message => message.Contains("task completed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_DiagnosticRun_LogsCancellationWithLastStage()
    {
        const string diagnosticRunId = "report-canceled";
        var logger = new Mock<ILogger<MediaCleanupTask>>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var task = new MediaCleanupTask(
            logger.Object,
            Mock.Of<ILocalizationManager>(),
            new TestPolicyProvider(CreatePolicy(), requiresMigrationReview: false),
            new CancelingCatalogAdapter(),
            new CleanupPlanner(new FixedClock(), new OrdinalPathMatcher(), new NoExtraFileProbe()),
            new RecordingMutationAdapter(),
            diagnosticRunId)
        {
            IsDryRun = true,
        };

        var action = () => task.ExecuteAsync(new Progress<double>(), cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        task.DiagnosticStage.Should().Be("catalog snapshot");
        logger.Messages().Should().Contain(message =>
            message.Contains(diagnosticRunId, StringComparison.Ordinal)
            && message.Contains("canceled", StringComparison.Ordinal)
            && message.Contains("catalog snapshot", StringComparison.Ordinal));
    }

    [Fact]
    public void NotificationOverview_IncludesPathAndItemDecisionLog()
    {
        var item = CreateItem();
        var plan = new CleanupPlanner(new FixedClock(), new OrdinalPathMatcher(), new NoExtraFileProbe())
            .Plan(new CleanupRequest(CreatePolicy(), [new MediaUser("user", "User")], [item], false));

        plan.AuditEntries.Should().BeEmpty();
        var notificationDecision = JellyfinMutationAdapter.WithNotificationOverview(plan.Decisions.Single());
        var overview = notificationDecision.Notification.Overview;

        overview.Should().Contain("Path:");
        overview.Should().Contain(@"C:\Media\Movie.mkv");
        overview.Should().Contain("Decision:");
        overview.Should().Contain("Reason: expired for User");
        overview.Should().Contain("Matched rules: rule");
        overview.Should().Contain("Result: successfully deleted");
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

    private static MediaCleanupTask CreateTask(
        bool requiresMigrationReview,
        RecordingMutationAdapter mutation,
        bool isDryRun = false,
        Mock<ILogger<MediaCleanupTask>>? logger = null,
        string? diagnosticRunId = null) =>
        new(
            logger?.Object ?? Mock.Of<ILogger<MediaCleanupTask>>(),
            Mock.Of<ILocalizationManager>(),
            new TestPolicyProvider(CreatePolicy(), requiresMigrationReview),
            new TestCatalogAdapter(CreateItem()),
            new CleanupPlanner(new FixedClock(), new OrdinalPathMatcher(), new NoExtraFileProbe()),
            mutation,
            diagnosticRunId)
        {
            IsDryRun = isDryRun,
        };

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

    private sealed class CancelingCatalogAdapter : IMediaCatalogAdapter
    {
        public CleanupCatalog Create(CleanupPolicy policy, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Expected a canceled token.");
        }
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
