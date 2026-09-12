using System.Reflection;
using System.Text;
using FluentAssertions;
using MediaCleaner.Controllers;
using MediaCleaner.Core;
using MediaBrowser.Common;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace MediaCleaner.Tests;

public class TroubleshootingControllerTests
{
    [Fact]
    public void ReportCache_KeepsMultipleReportsAndUnknownIdsDoNotEvictThem()
    {
        var cache = new TroubleshootingReportCache(TimeSpan.FromMinutes(15), maxReports: 3);
        var now = new DateTime(2026, 07, 11, 12, 00, 00, DateTimeKind.Utc);
        var first = CreateCachedReport("first", now);
        var second = CreateCachedReport("second", now.AddMinutes(1));

        cache.Set(first, now);
        cache.Set(second, now.AddMinutes(1));

        cache.TryGet("missing", now.AddMinutes(2), out _).Should().BeFalse();
        cache.TryGet("first", now.AddMinutes(2), out var cachedFirst).Should().BeTrue();
        cache.TryGet("second", now.AddMinutes(2), out var cachedSecond).Should().BeTrue();
        cachedFirst.Should().BeSameAs(first);
        cachedSecond.Should().BeSameAs(second);
    }

    [Fact]
    public void ReportCache_EvictsOldestReportWhenBoundIsExceeded()
    {
        var cache = new TroubleshootingReportCache(TimeSpan.FromMinutes(15), maxReports: 2);
        var now = new DateTime(2026, 07, 11, 12, 00, 00, DateTimeKind.Utc);

        cache.Set(CreateCachedReport("first", now), now);
        cache.Set(CreateCachedReport("second", now.AddMinutes(1)), now.AddMinutes(1));
        cache.Set(CreateCachedReport("third", now.AddMinutes(2)), now.AddMinutes(2));

        cache.TryGet("first", now.AddMinutes(3), out _).Should().BeFalse();
        cache.TryGet("second", now.AddMinutes(3), out _).Should().BeTrue();
        cache.TryGet("third", now.AddMinutes(3), out _).Should().BeTrue();
    }

    [Fact]
    public void ReportCache_ExpiresReportsByCreatedTime()
    {
        var cache = new TroubleshootingReportCache(TimeSpan.FromMinutes(15), maxReports: 3);
        var created = new DateTime(2026, 07, 11, 12, 00, 00, DateTimeKind.Utc);
        cache.Set(CreateCachedReport("first", created), created);

        cache.TryGet("first", created.AddMinutes(15), out _).Should().BeTrue();
        cache.TryGet("first", created.AddMinutes(15).AddTicks(1), out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetReport_WithCachedReportId_ReturnsSnapshotWithoutCreatingDryRunScope()
    {
        var reportId = Guid.NewGuid().ToString("N");
        var report = CreateCachedReport(reportId, DateTime.UtcNow);
        typeof(TroubleshootingController)
            .GetMethod("SetCachedReport", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [report]);
        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        var applicationHost = new Mock<IApplicationHost>(MockBehavior.Strict);
        var controller = new TroubleshootingController(scopeFactory.Object, applicationHost.Object);

        var result = await controller.GetReport(reportId);

        result.Result.Should().BeNull();
        result.Value.Should().NotBeNull();
        result.Value!.ReportId.Should().Be(reportId);
        scopeFactory.VerifyNoOtherCalls();
        applicationHost.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetReport_WithUnknownReportId_ReturnsNotFoundWithoutCreatingDryRunScope()
    {
        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        var applicationHost = new Mock<IApplicationHost>(MockBehavior.Strict);
        var controller = new TroubleshootingController(scopeFactory.Object, applicationHost.Object);

        var result = await controller.GetReport(Guid.NewGuid().ToString("N"));

        result.Value.Should().BeNull();
        result.Result.Should().BeOfType<NotFoundObjectResult>();
        scopeFactory.VerifyNoOtherCalls();
        applicationHost.VerifyNoOtherCalls();
    }

    [Fact]
    public void StatusFormatter_CalculatesNextRunFromDailyTrigger()
    {
        var now = new DateTime(2026, 06, 27, 12, 00, 00, DateTimeKind.Utc);
        var trigger = new TaskTriggerInfo
        {
#if JELLYFIN_10_10
            Type = TaskTriggerInfo.TriggerDaily,
#else
            Type = TaskTriggerInfoType.DailyTrigger,
#endif
            TimeOfDayTicks = TimeSpan.FromHours(22).Ticks,
        };

        GetNextRunUtc(trigger, null, now).Should().Be(new DateTime(2026, 06, 27, 22, 00, 00, DateTimeKind.Utc));
    }

    [Fact]
    public void StatusFormatter_CalculatesNextRunFromIntervalTriggerAfterLastExecution()
    {
        var now = new DateTime(2026, 06, 27, 12, 00, 00, DateTimeKind.Utc);
        var trigger = new TaskTriggerInfo
        {
#if JELLYFIN_10_10
            Type = TaskTriggerInfo.TriggerInterval,
#else
            Type = TaskTriggerInfoType.IntervalTrigger,
#endif
            IntervalTicks = TimeSpan.FromDays(1).Ticks,
        };
        var lastExecutionResult = new TaskResult
        {
            EndTimeUtc = new DateTime(2026, 06, 26, 22, 00, 00, DateTimeKind.Utc),
        };

        GetNextRunUtc(trigger, lastExecutionResult, now).Should().Be(new DateTime(2026, 06, 27, 22, 00, 00, DateTimeKind.Utc));
    }

    [Fact]
    public void TroubleshootingReportFormatters_CreateUiHtmlAndGitHubMarkdown()
    {
        var evidence = new CleanupAuditEvidence(
            new DateTime(2026, 07, 20, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 07, 18, 12, 0, 0, DateTimeKind.Utc),
            null,
            [new PlaybackState("alice", new DateTime(2026, 07, 14, 12, 0, 0, DateTimeKind.Utc), true, false, false, "Alice")]);
        var plan = new CleanupPlan(
            [],
            [],
            [
                new CleanupAuditEntry(
                    null,
                    null,
                    null,
                    "rule-1",
                    "played rule",
                    CleanupRuleActionKind.Delete,
                    CleanupAuditStage.RuleEligibility,
                    CleanupAuditOutcome.Skipped,
                    "rule skipped"),
                new CleanupAuditEntry(
                    "m1",
                    "The Show | S01E02 | Pilot",
                    MediaItemKind.Episode,
                    "rule-2",
                    "cleanup rule",
                    CleanupRuleActionKind.Delete,
                    CleanupAuditStage.Trigger,
                    CleanupAuditOutcome.Matched,
                    "trigger matched",
                    evidence),
                new CleanupAuditEntry(
                    "m1",
                    "The Show | S01E02 | Pilot",
                    MediaItemKind.Episode,
                    "rule-2",
                    "cleanup rule",
                    CleanupRuleActionKind.Delete,
                    CleanupAuditStage.Protection,
                    CleanupAuditOutcome.Suppressed,
                    "delete suppressed"),
            ]);
        var config = "<PluginConfiguration><Rule>value</Rule></PluginConfiguration>";

        var htmlMethod = typeof(TroubleshootingController).GetMethod("BuildFormattedHtml", BindingFlags.NonPublic | BindingFlags.Static)!;
        var html = (string)htmlMethod.Invoke(null, ["10.11.0", "3.0.0", config, plan])!;

        html.Should().Contain("Rule-level decisions");
        html.Should().Contain("Item-level decisions");
        html.Should().Contain("Outcome legend");
        html.Should().Contain("<details class=\"mediaCleanerDecisionGroup\" open>");
        html.Should().Contain("mediaCleanerItemDecisionGroup");
        html.Should().Contain("mediaCleanerResultBadge-suppressed");
        html.Should().Contain("A delete rule matched this item, but protection overrode that delete decision.");
        html.Should().Contain("<p class=\"mediaCleanerDecisionLastStage\"><strong>Last stage:</strong> Protection -&gt; Suppressed: delete suppressed</p>");
        html.Should().Contain("Deletion was stopped by a safety blocker, such as an unresolved series exception");
        html.Should().Contain("The Show | S01E02 | Pilot");
        html.Should().Contain("played rule");
        html.Should().Contain("Deletion was stopped by a safety blocker");
        html.Should().Contain("View current rule");
        html.Should().Contain("Alice");
        html.Should().Contain("data-media-cleaner-utc");
        html.Should().NotContain("[Inf]");

        var markdownMethod = typeof(TroubleshootingController).GetMethod("BuildIssueMarkdown", BindingFlags.NonPublic | BindingFlags.Static)!;
        var markdown = (string)markdownMethod.Invoke(null, ["10.11.0", "3.0.0", config, plan])!;

        markdown.Should().Contain("### Environment");
        markdown.Should().Contain("### Dry-run summary");
        markdown.Should().Contain("<details>");
        markdown.Should().Contain("<summary>Configuration</summary>");
        markdown.Should().Contain("```xml");
        markdown.Should().Contain("### Rule-level decisions");
        markdown.Should().Contain("### Item-level decisions");
        markdown.Should().Contain("Episode: The Show | S01E02 | Pilot (m1) - Suppressed");
        markdown.Should().Contain("**Last stage:** Protection -&gt; Suppressed: delete suppressed");
        markdown.Should().Contain("User 1: played");
        markdown.Should().Contain("2026-07-14T12:00:00.0000000Z");
        markdown.Should().NotContain("Alice");
        markdown.Should().NotContain("Outcome legend");
        markdown.Should().NotContain("mediaCleaner");
        markdown.Should().NotContain("mediaCleanerDecisionBadge");
    }

    [Fact]
    public void TroubleshootingDecisionFormatter_UsesUserFacingIssue116Outcomes()
    {
        var entries = new[]
        {
            Entry("arcane", "protect", CleanupRuleActionKind.Protect, CleanupAuditStage.Trigger, CleanupAuditOutcome.Matched),
            Entry("arcane", "protect", CleanupRuleActionKind.Protect, CleanupAuditStage.FavoriteFilter, CleanupAuditOutcome.Rejected),
            Entry("blue", "protect", CleanupRuleActionKind.Protect, CleanupAuditStage.Trigger, CleanupAuditOutcome.Matched),
            Entry("blue", "protect", CleanupRuleActionKind.Protect, CleanupAuditStage.Protection, CleanupAuditOutcome.Protected),
            Entry("new-world", "cleanup", CleanupRuleActionKind.Delete, CleanupAuditStage.Trigger, CleanupAuditOutcome.Matched),
            Entry("new-world", "protect", CleanupRuleActionKind.Protect, CleanupAuditStage.Trigger, CleanupAuditOutcome.Matched),
            Entry("new-world", "protect", CleanupRuleActionKind.Protect, CleanupAuditStage.Protection, CleanupAuditOutcome.Protected),
            Entry("new-world", "cleanup", CleanupRuleActionKind.Delete, CleanupAuditStage.Protection, CleanupAuditOutcome.Suppressed),
        };

        var groups = TroubleshootingDecisionFormatter.BuildItemGroups(new CleanupPlan([], [], entries));

        groups.Single(x => x.ItemId == "arcane").FinalOutcome.Should().Be(ItemReportOutcome.NoAction);
        groups.Single(x => x.ItemId == "arcane").RuleGroups.Single().Matched.Should().BeFalse();
        groups.Single(x => x.ItemId == "blue").FinalOutcome.Should().Be(ItemReportOutcome.NoAction);
        groups.Single(x => x.ItemId == "blue").RuleGroups.Single().Matched.Should().BeTrue();
        groups.Single(x => x.ItemId == "new-world").FinalOutcome.Should().Be(ItemReportOutcome.Suppressed);
    }

    [Fact]
    public void TroubleshootingDecisionFormatter_RendersRuleSnapshotEvidenceAndPrivateMarkdown()
    {
        var rule = new CleanupRule(
            "cleanup",
            "Movies played 14d ago",
            true,
            new CleanupRuleTrigger(CleanupRuleTriggerKind.Played, 14),
            new CleanupRuleFilters(
                [MediaItemKind.Movie],
                ["alice"],
                UsersListMode.Acknowledge,
                ["bob", "carol"],
                UsersListMode.Acknowledge,
                RuleFavoriteFilterKind.FavoriteByAnyUser,
                [],
                LocationsListMode.Exclude,
                false,
                TagMode.Exclusion,
                [],
                SeriesDeleteKind.Episode,
                SeriesKeepKind.None),
            new CleanupRuleActions(CleanupRuleActionKind.Delete, false));
        var evidence = new CleanupAuditEvidence(
            new DateTime(2026, 07, 1, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 07, 25, 12, 0, 0, DateTimeKind.Utc),
            null,
            [
                new PlaybackState("alice", new DateTime(2026, 07, 20, 12, 0, 0, DateTimeKind.Utc), true, false, false, "Alice"),
                new PlaybackState("bob", null, false, false, false, "Bob"),
                new PlaybackState("carol", null, false, false, true, "Carol", FavoriteSource: FavoriteSourceKind.Season),
            ]);
        var entry = new CleanupAuditEntry(
            "movie",
            "Movie",
            MediaItemKind.Movie,
            rule.Id,
            rule.Name,
            CleanupRuleActionKind.Delete,
            CleanupAuditStage.Trigger,
            CleanupAuditOutcome.Matched,
            "trigger matched",
            evidence);
        var groups = TroubleshootingDecisionFormatter.BuildItemGroups(
            new CleanupPlan([], [], [entry]),
            new CleanupPolicy([rule], false));
        var group = groups.Single();

        var html = new StringBuilder();
        TroubleshootingDecisionFormatter.AppendItemHtml(
            html,
            group,
            new Dictionary<string, string> { ["alice"] = "Alice", ["bob"] = "Bob", ["carol"] = "Carol" });
        html.ToString().Should().Contain("Movies played 14d ago");
        html.ToString().Should().Contain("Filters (AND)");
        html.ToString().Should().Contain("episodes inherit favorite from season or series");
        html.ToString().Should().Contain("data-view-rule-id=\"cleanup\"");
        html.ToString().Should().Contain("Evaluated at");
        html.ToString().Should().Contain("Alice</strong> <span class=\"mediaCleanerEvidenceRoles\">(playback trigger)</span>: played; Last played");
        html.ToString().Should().Contain("Bob</strong> <span class=\"mediaCleanerEvidenceRoles\">(favorite filter)</span>: not played; not favorite");
        html.ToString().Should().Contain("Carol</strong> <span class=\"mediaCleanerEvidenceRoles\">(favorite filter)</span>: not played; favorite via season");
        html.ToString().Should().Contain("favorite via season");

        var markdown = new StringBuilder();
        TroubleshootingDecisionFormatter.AppendItemMarkdown(
            markdown,
            group,
            new Dictionary<string, string> { ["alice"] = "User 1", ["bob"] = "User 2", ["carol"] = "User 3" });
        markdown.ToString().Should().Contain("User 1");
        markdown.ToString().Should().Contain("User 2");
        markdown.ToString().Should().Contain("User 3");
        markdown.ToString().Should().NotContain("Alice");
        markdown.ToString().Should().NotContain("Bob");
        markdown.ToString().Should().NotContain("Carol");
        markdown.ToString().Should().Contain("Evaluated at: 2026-08-08T12:00:00.0000000Z");
        markdown.ToString().Should().Contain("User 1 (playback trigger): played");
        markdown.ToString().Should().Contain("User 1 (playback trigger): played; Last played 2026-07-20T12:00:00.0000000Z; threshold 5.0 day(s) past");
        markdown.ToString().Should().Contain("User 2 (favorite filter): not played; not favorite");
        markdown.ToString().Should().Contain("User 3 (favorite filter): not played; favorite via season");
        markdown.ToString().Should().Contain("past");
    }

    [Fact]
    public void TroubleshootingDecisionFormatter_ResolvesEquivalentGuidFormatsAndLabelsMissingUsers()
    {
        var knownUserId = Guid.Parse("0bfdd89f-115e-41f7-9cb5-8ddad96189f0");
        var missingUserId = Guid.Parse("1e8e2828-70ac-4646-a98f-395844794afa");
        var rule = new CleanupRule(
            "cleanup",
            "Cleanup",
            true,
            new CleanupRuleTrigger(CleanupRuleTriggerKind.Played, 0),
            new CleanupRuleFilters(
                [MediaItemKind.Episode],
                [$" {knownUserId:N} "],
                UsersListMode.Acknowledge,
                [missingUserId.ToString("N")],
                UsersListMode.Acknowledge,
                RuleFavoriteFilterKind.NotFavoriteByAnyUser,
                [],
                LocationsListMode.Exclude,
                false,
                TagMode.Exclusion,
                [],
                SeriesDeleteKind.Episode,
                SeriesKeepKind.None),
            new CleanupRuleActions(CleanupRuleActionKind.Delete, false));
        var evidence = new CleanupAuditEvidence(
            new DateTime(2026, 09, 1, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 09, 12, 12, 0, 0, DateTimeKind.Utc),
            null,
            [new PlaybackState(knownUserId.ToString("D"), new DateTime(2026, 09, 1, 12, 0, 0, DateTimeKind.Utc), true, false, false)]);
        var entry = new CleanupAuditEntry(
            "episode",
            "Episode",
            MediaItemKind.Episode,
            rule.Id,
            rule.Name,
            CleanupRuleActionKind.Delete,
            CleanupAuditStage.Trigger,
            CleanupAuditOutcome.Matched,
            "trigger matched",
            evidence);
        var group = TroubleshootingDecisionFormatter.BuildItemGroups(
            new CleanupPlan([], [], [entry]),
            new CleanupPolicy([rule], false)).Single();

        var html = new StringBuilder();
        TroubleshootingDecisionFormatter.AppendItemHtml(
            html,
            group,
            new Dictionary<string, string> { [knownUserId.ToString("D").ToUpperInvariant()] = "Alice" });

        html.ToString().Should().Contain("played by at least one user in Alice at least 0 day(s) ago");
        html.ToString().Should().Contain($"unknown or deleted user ({missingUserId:N})");
        html.ToString().Should().Contain("Alice</strong> <span class=\"mediaCleanerEvidenceRoles\">(playback trigger)</span>");
    }

    private static CleanupAuditEntry Entry(
        string itemId,
        string ruleId,
        CleanupRuleActionKind action,
        CleanupAuditStage stage,
        CleanupAuditOutcome outcome) =>
        new(
            itemId,
            itemId,
            MediaItemKind.Movie,
            ruleId,
            ruleId,
            action,
            stage,
            outcome,
            $"{stage} {outcome}");

    private static DateTime? GetNextRunUtc(TaskTriggerInfo trigger, TaskResult? lastExecutionResult, DateTime nowUtc)
    {
        var method = typeof(TroubleshootingController).GetMethod("GetNextRunUtc", BindingFlags.NonPublic | BindingFlags.Static, [typeof(TaskTriggerInfo), typeof(TaskResult), typeof(DateTime)])!;
        return (DateTime?)method.Invoke(null, [trigger, lastExecutionResult, nowUtc]);
    }

    private static CachedTroubleshootingReport CreateCachedReport(string reportId, DateTime createdUtc) =>
        new(
            reportId,
            "10.11.0",
            "3.0.0",
            "<config />",
            new CleanupPolicy([], false),
            [],
            new Dictionary<string, string>(),
            CleanupPlan.Empty,
            [],
            createdUtc);
}
