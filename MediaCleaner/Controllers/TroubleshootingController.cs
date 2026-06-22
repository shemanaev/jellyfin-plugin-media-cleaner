using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using System.Xml;
using System.Xml.Serialization;
using MediaBrowser.Common;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using MediaCleaner.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.Controllers;

[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Route("MediaCleaner")]
public class TroubleshootingController(
    IServiceScopeFactory scopeFactory,
    IApplicationHost applicationHost
) : ControllerBase
{
    private const string CleanupTaskKey = "MediaCleanup";

    [HttpGet("Status")]
    [Produces(MediaTypeNames.Application.Json)]
    public MediaCleanerStatusResponse GetStatus()
    {
        using var scope = scopeFactory.CreateScope();
        var taskManager = scope.ServiceProvider.GetService<ITaskManager>();
        var scheduledTask = taskManager?.ScheduledTasks.FirstOrDefault(IsMediaCleanupTask);
        var policy = Plugin.Instance!.Configuration.ToCleanupPolicy();
        var activeCleanupRuleCount = policy.Rules.Count(x =>
            x.Enabled
            && x.Trigger.Days >= 0
            && x.Actions.Kind == CleanupRuleActionKind.Delete);

        return new MediaCleanerStatusResponse(
            activeCleanupRuleCount,
            scheduledTask is not null,
            scheduledTask?.State.ToString(),
            GetNextRunUtc(scheduledTask));
    }

    [HttpGet("Report")]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<TroubleshootingReportResponse> GetReport()
    {
        using var scope = scopeFactory.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<IUserManager>();
        var libraryManager = scope.ServiceProvider.GetRequiredService<ILibraryManager>();
        var userDataManager = scope.ServiceProvider.GetRequiredService<IUserDataManager>();
        var activityManager = scope.ServiceProvider.GetRequiredService<IActivityManager>();
        var localization = scope.ServiceProvider.GetRequiredService<ILocalizationManager>();
        var fileSystem = scope.ServiceProvider.GetRequiredService<IFileSystem>();
        var progress = new Progress<double>();

        using var loggerFactory = LoggerFactory.Create(_ => { });

        var task = new MediaCleanupTask(userManager, loggerFactory, libraryManager, userDataManager, activityManager, localization, fileSystem)
        {
            IsDryRun = true
        };
        await task.ExecuteAsync(progress, default!);

        var pluginConfig = GetPrettyXml(Plugin.Instance!.Configuration);
        var plan = task.LastPlan ?? CleanupPlan.Empty;

        return new TroubleshootingReportResponse(
            BuildFormattedHtml(applicationHost.ApplicationVersionString, Plugin.Instance.Version.ToString(), pluginConfig, plan),
            BuildIssueMarkdown(applicationHost.ApplicationVersionString, Plugin.Instance.Version.ToString(), pluginConfig, plan));
    }

    [HttpGet("ConfigBackup")]
    [Produces("application/xml")]
    public FileContentResult GetConfigurationBackup()
    {
        var pluginConfig = GetPrettyXml(Plugin.Instance!.Configuration);
        var fileName = $"media-cleaner-config-backup-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.xml";
        return File(Encoding.UTF8.GetBytes(pluginConfig), "application/xml", fileName);
    }

    private static bool IsMediaCleanupTask(IScheduledTaskWorker task) =>
        string.Equals(task.ScheduledTask.Key, CleanupTaskKey, StringComparison.OrdinalIgnoreCase)
        || string.Equals(task.LastExecutionResult?.Key, CleanupTaskKey, StringComparison.OrdinalIgnoreCase)
        || string.Equals(task.Name, "Played media cleanup", StringComparison.OrdinalIgnoreCase);

    private static DateTime? GetNextRunUtc(IScheduledTaskWorker? task)
    {
        if (task is null || task.State == TaskState.Running)
        {
            return null;
        }

        var nowUtc = DateTime.UtcNow;
        var candidates = task.Triggers
            .Select(trigger => GetNextRunUtc(trigger, task.LastExecutionResult, nowUtc))
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToList();

        return candidates.Count == 0 ? null : candidates.Min();
    }

    private static DateTime? GetNextRunUtc(TaskTriggerInfo trigger, TaskResult? lastExecutionResult, DateTime nowUtc)
    {
        var triggerType = trigger.Type.ToString();
        if (string.Equals(triggerType, "IntervalTrigger", StringComparison.OrdinalIgnoreCase)
            || string.Equals(triggerType, "Interval", StringComparison.OrdinalIgnoreCase))
        {
            if (trigger.IntervalTicks.GetValueOrDefault() <= 0)
            {
                return null;
            }

            var interval = TimeSpan.FromTicks(trigger.IntervalTicks.GetValueOrDefault());
            var anchor = lastExecutionResult?.EndTimeUtc ?? nowUtc;
            var next = anchor + interval;
            while (next <= nowUtc)
            {
                next += interval;
            }

            return DateTime.SpecifyKind(next, DateTimeKind.Utc);
        }

        if (string.Equals(triggerType, "DailyTrigger", StringComparison.OrdinalIgnoreCase)
            || string.Equals(triggerType, "Daily", StringComparison.OrdinalIgnoreCase))
        {
            return NextDailyRunUtc(trigger.TimeOfDayTicks.GetValueOrDefault(), nowUtc);
        }

        if (string.Equals(triggerType, "WeeklyTrigger", StringComparison.OrdinalIgnoreCase)
            || string.Equals(triggerType, "Weekly", StringComparison.OrdinalIgnoreCase))
        {
            return NextWeeklyRunUtc(trigger.DayOfWeek, trigger.TimeOfDayTicks.GetValueOrDefault(), nowUtc);
        }

        return null;
    }

    private static DateTime NextDailyRunUtc(long timeOfDayTicks, DateTime nowUtc)
    {
        var timeOfDay = TimeOfDay(timeOfDayTicks);
        var next = nowUtc.Date + timeOfDay;
        return next <= nowUtc ? next.AddDays(1) : next;
    }

    private static DateTime NextWeeklyRunUtc(DayOfWeek? dayOfWeek, long timeOfDayTicks, DateTime nowUtc)
    {
        var targetDay = dayOfWeek ?? nowUtc.DayOfWeek;
        var daysUntilTarget = ((int)targetDay - (int)nowUtc.DayOfWeek + 7) % 7;
        var next = nowUtc.Date.AddDays(daysUntilTarget) + TimeOfDay(timeOfDayTicks);
        return next <= nowUtc ? next.AddDays(7) : next;
    }

    private static TimeSpan TimeOfDay(long ticks) =>
        ticks <= 0 ? TimeSpan.Zero : TimeSpan.FromTicks(ticks % TimeSpan.TicksPerDay);

    private static string BuildFormattedHtml(string jellyfinVersion, string pluginVersion, string pluginConfig, CleanupPlan plan)
    {
        var decisionReport = BuildDecisionReport(plan);
        return $@"<div class=""mediaCleanerTroubleshootingReport"">
<ul class=""mediaCleanerReportMeta"">
<li><strong>Jellyfin version:</strong> {HttpUtility.HtmlEncode(jellyfinVersion)}</li>
<li><strong>Plugin version:</strong> {HttpUtility.HtmlEncode(pluginVersion)}</li>
</ul>
<details>
<summary>Configuration</summary>
<pre>
{HttpUtility.HtmlEncode(pluginConfig)}
</pre>
</details>
<details open>
<summary>Decision report</summary>
{decisionReport}
</details>
</div>
";
    }

    private static string BuildDecisionReport(CleanupPlan plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<div class=\"mediaCleanerDecisionReport\">");
        builder.AppendLine("<div class=\"mediaCleanerDecisionSummary\">");
        AppendMetric(builder, "Final delete decisions", plan.Decisions.Count.ToString(CultureInfo.InvariantCulture));
        AppendMetric(
            builder,
            "Planned deletion operations",
            CountPlannedDeletionOperations(plan).ToString(CultureInfo.InvariantCulture));
        AppendMetric(builder, "Audit entries", plan.AuditEntries.Count.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("</div>");

        builder.AppendLine("<section class=\"mediaCleanerDecisionSection\">");
        builder.AppendLine("<h3>Outcome summary</h3>");
        builder.AppendLine("<div class=\"mediaCleanerOutcomeSummary\">");
        foreach (var outcome in Enum.GetValues<CleanupAuditOutcome>())
        {
            var count = plan.AuditEntries.Count(x => x.Outcome == outcome);
            if (count == 0)
            {
                continue;
            }

            builder.Append("<span class=\"mediaCleanerDecisionBadge ");
            builder.Append(GetOutcomeClass(outcome));
            builder.Append("\">");
            builder.Append(HttpUtility.HtmlEncode(outcome.ToString()));
            builder.Append(": ");
            builder.Append(count.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("</span>");
        }
        builder.AppendLine("</div>");
        builder.AppendLine("</section>");
        AppendOutcomeLegend(builder);

        var ruleEntries = plan.AuditEntries
            .Where(x => x.ItemId is null)
            .GroupBy(x => x.RuleId ?? x.RuleName ?? string.Empty)
            .OrderBy(x => x.First().RuleName);
        if (ruleEntries.Any())
        {
            builder.AppendLine("<section class=\"mediaCleanerDecisionSection\">");
            builder.AppendLine("<h3>Rule-level decisions</h3>");
            foreach (var group in ruleEntries)
            {
                var first = group.First();
                builder.AppendLine("<details class=\"mediaCleanerDecisionGroup\" open>");
                builder.Append("<summary>");
                builder.Append(HttpUtility.HtmlEncode(first.RuleName ?? first.RuleId ?? "Unknown rule"));
                builder.AppendLine("</summary>");
                builder.AppendLine("<ol class=\"mediaCleanerDecisionList\">");
                foreach (var entry in group)
                {
                    AppendAuditEntry(builder, entry);
                }
                builder.AppendLine("</ol>");
                builder.AppendLine("</details>");
            }
            builder.AppendLine("</section>");
        }

        var itemEntries = plan.AuditEntries
            .Where(x => x.ItemId is not null)
            .GroupBy(x => x.ItemId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.First().ItemKind?.ToString())
            .ThenBy(x => x.First().ItemName);
        if (itemEntries.Any())
        {
            builder.AppendLine("<section class=\"mediaCleanerDecisionSection\">");
            builder.AppendLine("<h3>Item-level decisions</h3>");
            foreach (var group in itemEntries)
            {
                var first = group.First();
                var finalOutcome = CleanupAuditFormatter.GetFinalOutcome(group);
                builder.AppendLine("<details class=\"mediaCleanerDecisionGroup\">");
                builder.Append("<summary>");
                builder.Append("<span class=\"mediaCleanerDecisionItemTitle\">");
                builder.Append(HttpUtility.HtmlEncode($"{first.ItemKind}: {first.ItemName}"));
                builder.Append("</span> ");
                builder.Append("<span class=\"mediaCleanerDecisionItemId\">");
                builder.Append(HttpUtility.HtmlEncode(first.ItemId));
                builder.Append("</span> ");
                AppendOutcomeBadge(builder, finalOutcome);
                builder.AppendLine("</summary>");
                builder.AppendLine("<ol class=\"mediaCleanerDecisionList\">");
                foreach (var entry in group)
                {
                    AppendAuditEntry(builder, entry);
                }
                builder.AppendLine("</ol>");
                builder.AppendLine("</details>");
            }
            builder.AppendLine("</section>");
        }

        if (plan.AuditEntries.Count == 0)
        {
            builder.AppendLine("<p class=\"mediaCleanerDecisionEmpty\">No audit entries were produced. Check that at least one cleanup or protection rule is enabled.</p>");
        }

        builder.AppendLine("</div>");
        return builder.ToString();
    }

    private static string BuildIssueMarkdown(string jellyfinVersion, string pluginVersion, string pluginConfig, CleanupPlan plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine("### Environment");
        builder.AppendLine();
        builder.AppendLine($"- Jellyfin version: {jellyfinVersion}");
        builder.AppendLine($"- Plugin version: {pluginVersion}");
        builder.AppendLine();
        builder.AppendLine("### Dry-run summary");
        builder.AppendLine();
        builder.AppendLine($"- Final delete decisions: {plan.Decisions.Count.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"- Planned deletion operations: {CountPlannedDeletionOperations(plan).ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"- Audit entries: {plan.AuditEntries.Count.ToString(CultureInfo.InvariantCulture)}");
        foreach (var group in plan.AuditEntries.GroupBy(x => x.Outcome).OrderBy(x => x.Key.ToString()))
        {
            builder.AppendLine($"- {group.Key}: {group.Count().ToString(CultureInfo.InvariantCulture)}");
        }

        builder.AppendLine();
        builder.AppendLine("<details>");
        builder.AppendLine("<summary>Configuration</summary>");
        builder.AppendLine();
        builder.AppendLine("```xml");
        builder.AppendLine(EscapeMarkdownFence(pluginConfig));
        builder.AppendLine("```");
        builder.AppendLine("</details>");

        AppendIssueRuleDecisions(builder, plan);
        AppendIssueItemDecisions(builder, plan);

        if (plan.AuditEntries.Count == 0)
        {
            builder.AppendLine();
            builder.AppendLine("No audit entries were produced. Check that at least one cleanup or protection rule is enabled.");
        }

        return builder.ToString();
    }

    private static int CountPlannedDeletionOperations(CleanupPlan plan) =>
        plan.AuditEntries.Count(x => x.Stage == CleanupAuditStage.DeletionCascade && x.Outcome == CleanupAuditOutcome.Planned);

    private static void AppendIssueRuleDecisions(StringBuilder builder, CleanupPlan plan)
    {
        var ruleEntries = plan.AuditEntries
            .Where(x => x.ItemId is null)
            .GroupBy(x => x.RuleId ?? x.RuleName ?? string.Empty)
            .OrderBy(x => x.First().RuleName)
            .ToList();
        if (ruleEntries.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("### Rule-level decisions");
        foreach (var group in ruleEntries)
        {
            var first = group.First();
            builder.AppendLine();
            builder.AppendLine($"#### {EscapeMarkdownText(first.RuleName ?? first.RuleId ?? "Unknown rule")}");
            foreach (var entry in group)
            {
                AppendIssueAuditEntry(builder, entry);
            }
        }
    }

    private static void AppendIssueItemDecisions(StringBuilder builder, CleanupPlan plan)
    {
        var itemEntries = plan.AuditEntries
            .Where(x => x.ItemId is not null)
            .GroupBy(x => x.ItemId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.First().ItemKind?.ToString())
            .ThenBy(x => x.First().ItemName)
            .ToList();
        if (itemEntries.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("### Item-level decisions");
        foreach (var group in itemEntries)
        {
            var first = group.First();
            var finalOutcome = CleanupAuditFormatter.GetFinalOutcome(group);
            builder.AppendLine();
            builder.AppendLine("<details>");
            builder.AppendLine($"<summary>{EscapeMarkdownText($"{first.ItemKind}: {first.ItemName} ({first.ItemId}) - {finalOutcome}")}</summary>");
            builder.AppendLine();
            foreach (var entry in group)
            {
                AppendIssueAuditEntry(builder, entry);
            }

            builder.AppendLine("</details>");
        }
    }

    private static void AppendIssueAuditEntry(StringBuilder builder, CleanupAuditEntry entry)
    {
        builder.AppendLine($"- {CleanupAuditFormatter.FormatPlainTextEntry(entry, escapeText: EscapeMarkdownText)}");
    }

    private static string EscapeMarkdownFence(string value) => value.Replace("```", "`\u200b``", StringComparison.Ordinal);

    private static string EscapeMarkdownText(string value) =>
        value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static void AppendOutcomeLegend(StringBuilder builder)
    {
        builder.AppendLine("<section class=\"mediaCleanerDecisionSection\">");
        builder.AppendLine("<h3>Outcome legend</h3>");
        builder.AppendLine("<dl class=\"mediaCleanerOutcomeLegend\">");
        foreach (var outcome in Enum.GetValues<CleanupAuditOutcome>())
        {
            builder.AppendLine("<div class=\"mediaCleanerOutcomeLegendItem\">");
            builder.Append("<dt>");
            AppendOutcomeBadge(builder, outcome);
            builder.AppendLine("</dt>");
            builder.Append("<dd>");
            builder.Append(HttpUtility.HtmlEncode(GetOutcomeDescription(outcome)));
            builder.AppendLine("</dd>");
            builder.AppendLine("</div>");
        }

        builder.AppendLine("</dl>");
        builder.AppendLine("</section>");
    }

    private static string GetOutcomeDescription(CleanupAuditOutcome outcome) => outcome switch
    {
        CleanupAuditOutcome.Matched => "The item or aggregate passed this evaluation stage.",
        CleanupAuditOutcome.Rejected => "The item matched an earlier stage, but this filter or policy excluded it.",
        CleanupAuditOutcome.Protected => "A protection rule matched this item and marked it as protected.",
        CleanupAuditOutcome.Suppressed => "A delete rule matched this item, but protection overrode that delete decision.",
        CleanupAuditOutcome.Planned => "The item is part of the final deletion plan. In dry-run mode this is only a preview.",
        CleanupAuditOutcome.Blocked => "A deletion cascade or final operation was stopped by a safety blocker, such as a protected child or extra files.",
        CleanupAuditOutcome.Skipped => "The rule or stage was not evaluated because its prerequisites were not met.",
        _ => throw new NotSupportedException($"Unsupported cleanup audit outcome: {outcome}"),
    };

    private static void AppendMetric(StringBuilder builder, string label, string value)
    {
        builder.AppendLine("<div class=\"mediaCleanerDecisionMetric\">");
        builder.Append("<span>");
        builder.Append(HttpUtility.HtmlEncode(label));
        builder.AppendLine("</span>");
        builder.Append("<strong>");
        builder.Append(HttpUtility.HtmlEncode(value));
        builder.AppendLine("</strong>");
        builder.AppendLine("</div>");
    }

    private static void AppendAuditEntry(StringBuilder builder, CleanupAuditEntry entry)
    {
        builder.AppendLine("<li class=\"mediaCleanerDecisionEntry\">");
        builder.Append("<span class=\"mediaCleanerDecisionStage\">");
        builder.Append(HttpUtility.HtmlEncode(entry.Stage.ToString()));
        builder.Append("</span>");
        builder.Append("<span class=\"mediaCleanerDecisionArrow\">-&gt;</span>");
        AppendOutcomeBadge(builder, entry.Outcome);
        if (!string.IsNullOrEmpty(entry.RuleName))
        {
            builder.Append("<span class=\"mediaCleanerDecisionRule\">");
            builder.Append(HttpUtility.HtmlEncode(entry.RuleName));
            builder.Append("</span>");
        }

        builder.Append("<span class=\"mediaCleanerDecisionReason\">");
        builder.Append(HttpUtility.HtmlEncode(entry.Reason));
        builder.Append("</span>");
        builder.AppendLine("</li>");
    }

    private static void AppendOutcomeBadge(StringBuilder builder, CleanupAuditOutcome outcome)
    {
        builder.Append("<span class=\"mediaCleanerDecisionBadge ");
        builder.Append(GetOutcomeClass(outcome));
        builder.Append("\">");
        builder.Append(HttpUtility.HtmlEncode(outcome.ToString()));
        builder.Append("</span>");
    }

    private static string GetOutcomeClass(CleanupAuditOutcome outcome) =>
        $"mediaCleanerDecisionBadge-{outcome.ToString().ToLowerInvariant()}";

    private static string GetPrettyXml(object o)
    {
        using var memoryStream = new MemoryStream();
        var serializer = new XmlSerializer(o.GetType());
        var ns = new XmlSerializerNamespaces(new[] { XmlQualifiedName.Empty });
        var streamWriter = XmlWriter.Create(memoryStream, new()
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            OmitXmlDeclaration = true,
        });
        serializer.Serialize(streamWriter, o, ns);
        return Encoding.UTF8.GetString(memoryStream.ToArray());
    }
}

public sealed record TroubleshootingReportResponse(string FormattedHtml, string IssueMarkdown);

public sealed record MediaCleanerStatusResponse(
    int ActiveCleanupRuleCount,
    bool ScheduledTaskAvailable,
    string? ScheduledTaskState,
    DateTime? NextRunUtc);
