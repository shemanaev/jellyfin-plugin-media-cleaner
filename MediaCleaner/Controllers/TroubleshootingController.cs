using System;
using System.Collections.Generic;
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
    private const int MaxDetailedReportItemGroups = 500;
    private const int DefaultItemPageSize = 100;
    private const int MaxItemPageSize = 1000;
    private const int FullReportItemGroupThreshold = 100;
    private static readonly TimeSpan ReportCacheLifetime = TimeSpan.FromMinutes(15);
    private static readonly TroubleshootingReportCache reportCache = new(ReportCacheLifetime, maxReports: 3);

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
        await task.ExecuteAsync(progress, HttpContext.RequestAborted);

        var pluginConfig = GetPrettyXml(Plugin.Instance!.Configuration);
        var plan = task.LastPlan ?? CleanupPlan.Empty;
        var reportId = Guid.NewGuid().ToString("N");
        var jellyfinVersion = applicationHost.ApplicationVersionString;
        var pluginVersion = Plugin.Instance.Version.ToString();
        var itemGroups = BuildItemDecisionGroups(plan);
        var report = new CachedTroubleshootingReport(
            reportId,
            jellyfinVersion,
            pluginVersion,
            pluginConfig,
            plan,
            itemGroups,
            DateTime.UtcNow);

        SetCachedReport(report);

        return new TroubleshootingReportResponse(
            reportId,
            BuildFormattedHtmlPage(jellyfinVersion, pluginVersion, pluginConfig, plan, itemGroups, 0, DefaultItemPageSize),
            string.Empty,
            itemGroups.Count,
            itemGroups.Count,
            DefaultItemPageSize);
    }

    [HttpGet("ReportItems")]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<TroubleshootingReportItemsResponse> GetReportItems(
        [FromQuery] string reportId,
        [FromQuery] int start = 0,
        [FromQuery] int limit = DefaultItemPageSize,
        [FromQuery] string? search = null)
    {
        if (!TryGetCachedReport(reportId, out var report))
        {
            return NotFound(new { error = "Troubleshooting report has expired. Refresh the report and try again." });
        }

        var itemGroups = FilterItemDecisionGroups(report.ItemGroups, search);
        start = Math.Max(0, start);
        limit = ClampItemPageSize(limit);
        return new TroubleshootingReportItemsResponse(
            reportId,
            start,
            limit,
            itemGroups.Count,
            report.ItemGroups.Count,
            BuildItemDecisionReport(itemGroups, start, limit));
    }

    [HttpGet("ReportIssueSource")]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<TroubleshootingIssueSourceResponse> GetReportIssueSource([FromQuery] string reportId)
    {
        if (!TryGetCachedReport(reportId, out var report))
        {
            return NotFound(new { error = "Troubleshooting report has expired. Refresh the report and try again." });
        }

        var includeItemDetails = report.ItemGroups.Count <= FullReportItemGroupThreshold;
        return new TroubleshootingIssueSourceResponse(
            reportId,
            BuildIssueMarkdownCore(
                report.JellyfinVersion,
                report.PluginVersion,
                report.PluginConfig,
                report.Plan,
                report.ItemGroups,
                includeItemDetails));
    }

    [HttpGet("ReportIssueMarkdown")]
    public async Task<IActionResult> GetReportIssueMarkdown([FromQuery] string reportId)
    {
        if (!TryGetCachedReport(reportId, out var report))
        {
            return NotFound("Troubleshooting report has expired. Refresh the report and try again.");
        }

        var fileName = $"media-cleaner-troubleshooting-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.md";
        Response.ContentType = "text/markdown; charset=utf-8";
        Response.Headers["Content-Disposition"] = $"attachment; filename=\"{fileName}\"";
        await WriteIssueMarkdownAsync(Response.Body, report);
        return new EmptyResult();
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

    private static string BuildFormattedHtml(string jellyfinVersion, string pluginVersion, string pluginConfig, CleanupPlan plan) =>
        BuildFormattedHtmlPage(
            jellyfinVersion,
            pluginVersion,
            pluginConfig,
            plan,
            BuildItemDecisionGroups(plan),
            0,
            MaxDetailedReportItemGroups);

    private static string BuildFormattedHtmlPage(
        string jellyfinVersion,
        string pluginVersion,
        string pluginConfig,
        CleanupPlan plan,
        IReadOnlyList<ItemDecisionGroup> itemGroups,
        int itemStart,
        int itemLimit)
    {
        var decisionReport = BuildDecisionReport(plan, itemGroups, itemStart, itemLimit);
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

    private static string BuildDecisionReport(
        CleanupPlan plan,
        IReadOnlyList<ItemDecisionGroup> itemGroups,
        int itemStart,
        int itemLimit)
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

        AppendItemDecisionReport(builder, itemGroups, itemStart, itemLimit);

        if (plan.AuditEntries.Count == 0)
        {
            builder.AppendLine("<p class=\"mediaCleanerDecisionEmpty\">No audit entries were produced. Check that at least one cleanup or protection rule is enabled.</p>");
        }

        builder.AppendLine("</div>");
        return builder.ToString();
    }

    private static string BuildItemDecisionReport(IReadOnlyList<ItemDecisionGroup> itemGroups, int itemStart, int itemLimit)
    {
        var builder = new StringBuilder();
        AppendItemDecisionReport(builder, itemGroups, itemStart, itemLimit);
        return builder.ToString();
    }

    private static void AppendItemDecisionReport(
        StringBuilder builder,
        IReadOnlyList<ItemDecisionGroup> itemGroups,
        int itemStart,
        int itemLimit)
    {
        if (itemGroups.Count == 0)
        {
            return;
        }

        itemStart = Math.Max(0, itemStart);
        itemLimit = ClampItemPageSize(itemLimit);
        var endExclusive = Math.Min(itemGroups.Count, itemStart + itemLimit);

        builder.AppendLine("<section class=\"mediaCleanerDecisionSection\" id=\"MediaCleanerItemDecisionSection\">");
        builder.AppendLine("<h3>Item-level decisions</h3>");
        if (itemGroups.Count > itemLimit)
        {
            builder.Append("<p class=\"mediaCleanerDecisionNotice\">");
            builder.Append(HttpUtility.HtmlEncode($"Showing item-level decision groups {(itemStart + 1).ToString(CultureInfo.InvariantCulture)}-{endExclusive.ToString(CultureInfo.InvariantCulture)} of {itemGroups.Count.ToString(CultureInfo.InvariantCulture)}."));
            builder.AppendLine("</p>");
        }

        foreach (var group in itemGroups.Skip(itemStart).Take(itemLimit))
        {
            builder.AppendLine("<details class=\"mediaCleanerDecisionGroup\">");
            builder.Append("<summary>");
            builder.Append("<span class=\"mediaCleanerDecisionItemTitle\">");
            builder.Append(HttpUtility.HtmlEncode($"{group.ItemKind}: {group.ItemName}"));
            builder.Append("</span> ");
            builder.Append("<span class=\"mediaCleanerDecisionItemId\">");
            builder.Append(HttpUtility.HtmlEncode(group.ItemId));
            builder.Append("</span> ");
            AppendOutcomeBadge(builder, group.FinalOutcome);
            builder.AppendLine("</summary>");
            builder.AppendLine("<ol class=\"mediaCleanerDecisionList\">");
            foreach (var entry in group.Entries)
            {
                AppendAuditEntry(builder, entry);
            }

            builder.AppendLine("</ol>");
            builder.AppendLine("</details>");
        }

        builder.AppendLine("</section>");
    }

    private static string BuildIssueMarkdown(string jellyfinVersion, string pluginVersion, string pluginConfig, CleanupPlan plan) =>
        BuildIssueMarkdownCore(
            jellyfinVersion,
            pluginVersion,
            pluginConfig,
            plan,
            BuildItemDecisionGroups(plan),
            includeItemDetails: true);

    private static string BuildIssueMarkdownCore(
        string jellyfinVersion,
        string pluginVersion,
        string pluginConfig,
        CleanupPlan plan,
        IReadOnlyList<ItemDecisionGroup> itemGroups,
        bool includeItemDetails)
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
        if (includeItemDetails)
        {
            AppendIssueItemDecisions(builder, itemGroups, MaxDetailedReportItemGroups);
        }
        else if (itemGroups.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("### Item-level decisions");
            builder.AppendLine();
            builder.AppendLine($"Item-level decision groups are available in the full report. Total groups: {itemGroups.Count.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (plan.AuditEntries.Count == 0)
        {
            builder.AppendLine();
            builder.AppendLine("No audit entries were produced. Check that at least one cleanup or protection rule is enabled.");
        }

        return builder.ToString();
    }

    private static int CountPlannedDeletionOperations(CleanupPlan plan) =>
        plan.AuditEntries.Count(x => x.Stage == CleanupAuditStage.DeletionCascade && x.Outcome == CleanupAuditOutcome.Planned);

    private static int ClampItemPageSize(int value) => Math.Clamp(value, 1, MaxItemPageSize);

    private static bool TryGetCachedReport(string? reportId, out CachedTroubleshootingReport report)
    {
        if (string.IsNullOrWhiteSpace(reportId))
        {
            report = null!;
            return false;
        }

        return reportCache.TryGet(reportId, DateTime.UtcNow, out report);
    }

    private static void SetCachedReport(CachedTroubleshootingReport report)
    {
        reportCache.Set(report, DateTime.UtcNow);
    }

    private static async Task WriteIssueMarkdownAsync(Stream stream, CachedTroubleshootingReport report)
    {
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 16 * 1024, leaveOpen: true);
        await writer.WriteLineAsync("### Environment");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync($"- Jellyfin version: {report.JellyfinVersion}");
        await writer.WriteLineAsync($"- Plugin version: {report.PluginVersion}");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("### Dry-run summary");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync($"- Final delete decisions: {report.Plan.Decisions.Count.ToString(CultureInfo.InvariantCulture)}");
        await writer.WriteLineAsync($"- Planned deletion operations: {CountPlannedDeletionOperations(report.Plan).ToString(CultureInfo.InvariantCulture)}");
        await writer.WriteLineAsync($"- Audit entries: {report.Plan.AuditEntries.Count.ToString(CultureInfo.InvariantCulture)}");
        foreach (var group in report.Plan.AuditEntries.GroupBy(x => x.Outcome).OrderBy(x => x.Key.ToString()))
        {
            await writer.WriteLineAsync($"- {group.Key}: {group.Count().ToString(CultureInfo.InvariantCulture)}");
        }

        await writer.WriteLineAsync();
        await writer.WriteLineAsync("<details>");
        await writer.WriteLineAsync("<summary>Configuration</summary>");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("```xml");
        await writer.WriteLineAsync(EscapeMarkdownFence(report.PluginConfig));
        await writer.WriteLineAsync("```");
        await writer.WriteLineAsync("</details>");

        await WriteIssueRuleDecisionsAsync(writer, report.Plan);
        await WriteIssueItemDecisionsAsync(writer, report.ItemGroups);

        if (report.Plan.AuditEntries.Count == 0)
        {
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("No audit entries were produced. Check that at least one cleanup or protection rule is enabled.");
        }
    }

    private static async Task WriteIssueRuleDecisionsAsync(TextWriter writer, CleanupPlan plan)
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

        await writer.WriteLineAsync();
        await writer.WriteLineAsync("### Rule-level decisions");
        foreach (var group in ruleEntries)
        {
            var first = group.First();
            await writer.WriteLineAsync();
            await writer.WriteLineAsync($"#### {EscapeMarkdownText(first.RuleName ?? first.RuleId ?? "Unknown rule")}");
            foreach (var entry in group)
            {
                await writer.WriteLineAsync($"- {CleanupAuditFormatter.FormatPlainTextEntry(entry, escapeText: EscapeMarkdownText)}");
            }
        }
    }

    private static async Task WriteIssueItemDecisionsAsync(TextWriter writer, IReadOnlyList<ItemDecisionGroup> itemGroups)
    {
        if (itemGroups.Count == 0)
        {
            return;
        }

        await writer.WriteLineAsync();
        await writer.WriteLineAsync("### Item-level decisions");
        foreach (var group in itemGroups)
        {
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("<details>");
            await writer.WriteLineAsync($"<summary>{EscapeMarkdownText($"{group.ItemKind}: {group.ItemName} ({group.ItemId}) - {group.FinalOutcome}")}</summary>");
            await writer.WriteLineAsync();
            foreach (var entry in group.Entries)
            {
                await writer.WriteLineAsync($"- {CleanupAuditFormatter.FormatPlainTextEntry(entry, escapeText: EscapeMarkdownText)}");
            }

            await writer.WriteLineAsync("</details>");
        }
    }

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

    private static void AppendIssueItemDecisions(
        StringBuilder builder,
        IReadOnlyList<ItemDecisionGroup> itemGroups,
        int maxItemGroups)
    {
        if (itemGroups.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("### Item-level decisions");
        if (itemGroups.Count > maxItemGroups)
        {
            builder.AppendLine();
            builder.AppendLine($"Showing first {maxItemGroups.ToString(CultureInfo.InvariantCulture)} of {itemGroups.Count.ToString(CultureInfo.InvariantCulture)} item-level decision groups to keep the troubleshooting report small.");
        }

        foreach (var group in itemGroups.Take(maxItemGroups))
        {
            builder.AppendLine();
            builder.AppendLine("<details>");
            builder.AppendLine($"<summary>{EscapeMarkdownText($"{group.ItemKind}: {group.ItemName} ({group.ItemId}) - {group.FinalOutcome}")}</summary>");
            builder.AppendLine();
            foreach (var entry in group.Entries)
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

    private static IReadOnlyList<ItemDecisionGroup> BuildItemDecisionGroups(CleanupPlan plan) =>
        plan.AuditEntries
            .Where(x => x.ItemId is not null)
            .GroupBy(x => x.ItemId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var entries = group.ToList();
                var first = entries[0];
                return new ItemDecisionGroup(
                    first.ItemId ?? string.Empty,
                    first.ItemName ?? string.Empty,
                    first.ItemKind,
                    CleanupAuditFormatter.GetFinalOutcome(entries),
                    entries);
            })
            .OrderBy(x => x.ItemKind?.ToString())
            .ThenBy(x => x.ItemName)
            .ToList();

    private static IReadOnlyList<ItemDecisionGroup> FilterItemDecisionGroups(
        IReadOnlyList<ItemDecisionGroup> itemGroups,
        string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return itemGroups;
        }

        var value = search.Trim();
        return itemGroups
            .Where(group =>
                ContainsSearch(group.ItemName, value)
                || ContainsSearch(group.ItemId, value)
                || ContainsSearch(group.ItemKind?.ToString(), value))
            .ToList();
    }

    private static bool ContainsSearch(string? text, string search) =>
        !string.IsNullOrWhiteSpace(text)
        && text.Contains(search, StringComparison.OrdinalIgnoreCase);

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
        CleanupAuditOutcome.Blocked => "Deletion was stopped by a safety blocker, such as an unresolved series exception, a protected child, or extra files.",
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

public sealed record TroubleshootingReportResponse(
    string ReportId,
    string FormattedHtml,
    string IssueMarkdown,
    int ItemGroupCount,
    int TotalItemGroupCount,
    int ItemPageSize);

public sealed record TroubleshootingReportItemsResponse(
    string ReportId,
    int Start,
    int Limit,
    int ItemGroupCount,
    int TotalItemGroupCount,
    string FormattedHtml);

public sealed record TroubleshootingIssueSourceResponse(
    string ReportId,
    string IssueMarkdown);

internal sealed record CachedTroubleshootingReport(
    string ReportId,
    string JellyfinVersion,
    string PluginVersion,
    string PluginConfig,
    CleanupPlan Plan,
    IReadOnlyList<ItemDecisionGroup> ItemGroups,
    DateTime CreatedUtc);

internal sealed record ItemDecisionGroup(
    string ItemId,
    string ItemName,
    MediaItemKind? ItemKind,
    CleanupAuditOutcome FinalOutcome,
    IReadOnlyList<CleanupAuditEntry> Entries);

public sealed record MediaCleanerStatusResponse(
    int ActiveCleanupRuleCount,
    bool ScheduledTaskAvailable,
    string? ScheduledTaskState,
    DateTime? NextRunUtc);
