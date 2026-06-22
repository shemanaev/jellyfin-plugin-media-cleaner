using System.Reflection;
using FluentAssertions;
using MediaCleaner.Controllers;
using MediaCleaner.Core;
using MediaBrowser.Model.Tasks;

namespace MediaCleaner.Tests;

public class TroubleshootingControllerTests
{
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
                    "protect rule",
                    CleanupRuleActionKind.Protect,
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
        html.Should().Contain("<details class=\"mediaCleanerDecisionGroup\">");
        html.Should().Contain("mediaCleanerDecisionBadge-suppressed");
        html.Should().Contain("A delete rule matched this item, but protection overrode that delete decision.");
        html.Should().Contain("A deletion cascade or final operation was stopped by a safety blocker");
        html.Should().Contain("The Show | S01E02 | Pilot");
        html.Should().Contain("played rule");
        html.Should().Contain("delete suppressed");
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
        markdown.Should().Contain("- Protection -> Suppressed [protect rule]: delete suppressed");
        markdown.Should().NotContain("Outcome legend");
        markdown.Should().NotContain("mediaCleaner");
        markdown.Should().NotContain("mediaCleanerDecisionBadge");
    }

    private static DateTime? GetNextRunUtc(TaskTriggerInfo trigger, TaskResult? lastExecutionResult, DateTime nowUtc)
    {
        var method = typeof(TroubleshootingController).GetMethod("GetNextRunUtc", BindingFlags.NonPublic | BindingFlags.Static, [typeof(TaskTriggerInfo), typeof(TaskResult), typeof(DateTime)])!;
        return (DateTime?)method.Invoke(null, [trigger, lastExecutionResult, nowUtc]);
    }
}
