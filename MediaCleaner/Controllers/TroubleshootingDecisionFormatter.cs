using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Web;
using MediaCleaner.Core;

namespace MediaCleaner.Controllers;

internal static class TroubleshootingDecisionFormatter
{
    public static IReadOnlyList<ItemDecisionGroup> BuildItemGroups(CleanupPlan plan, CleanupPolicy? policy = null)
    {
        var rules = (policy?.Rules ?? [])
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        return plan.AuditEntries
            .Where(x => x.ItemId is not null)
            .GroupBy(x => x.ItemId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var entries = group.ToList();
                var first = entries[0];
                var ruleGroups = entries
                    .Where(x => !string.IsNullOrWhiteSpace(x.RuleId) || !string.IsNullOrWhiteSpace(x.RuleName))
                    .GroupBy(x => x.RuleId ?? x.RuleName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .Select(ruleGroup =>
                    {
                        var ruleEntries = ruleGroup.ToList();
                        var ruleId = ruleEntries[0].RuleId ?? string.Empty;
                        rules.TryGetValue(ruleId, out var rule);
                        var action = rule?.Actions.Kind ?? ruleEntries[0].Action;
                        var matched = ruleEntries.Any(x => x.Outcome is CleanupAuditOutcome.Matched
                                or CleanupAuditOutcome.Protected
                                or CleanupAuditOutcome.Suppressed
                                or CleanupAuditOutcome.Planned)
                            && !ruleEntries.Any(x => x.Outcome is CleanupAuditOutcome.Rejected
                                or CleanupAuditOutcome.Skipped
                                or CleanupAuditOutcome.Blocked);
                        return new RuleDecisionGroup(
                            ruleId,
                            rule?.Name ?? ruleEntries[0].RuleName ?? ruleId,
                            action,
                            matched,
                            rule,
                            ruleEntries);
                    })
                    .OrderBy(x => x.Action)
                    .ThenBy(x => x.RuleName)
                    .ToList();

                return new ItemDecisionGroup(
                    first.ItemId ?? string.Empty,
                    first.ItemName ?? string.Empty,
                    first.ItemKind,
                    GetFinalOutcome(entries),
                    ruleGroups,
                    entries);
            })
            .OrderBy(x => x.ItemKind?.ToString())
            .ThenBy(x => x.ItemName)
            .ToList();
    }

    public static void AppendItemHtml(
        StringBuilder builder,
        ItemDecisionGroup group,
        IReadOnlyDictionary<string, string> userNames)
    {
        builder.AppendLine("<details class=\"mediaCleanerDecisionGroup mediaCleanerItemDecisionGroup\">");
        builder.Append("<summary><span class=\"mediaCleanerDecisionItemTitle\">");
        builder.Append(Html($"{group.ItemKind}: {group.ItemName}"));
        builder.Append("</span> <span class=\"mediaCleanerDecisionItemId\">");
        builder.Append(Html(group.ItemId));
        builder.Append("</span> ");
        AppendResultBadge(builder, group.FinalOutcome);
        builder.AppendLine("</summary>");
        builder.Append("<p class=\"mediaCleanerDecisionResult\"><strong>Result:</strong> ");
        builder.Append(Html(GetResultDescription(group)));
        builder.AppendLine("</p>");
        AppendLastStageHtml(builder, group);

        foreach (var ruleGroup in group.RuleGroups)
        {
            AppendRuleHtml(builder, ruleGroup, userNames);
        }

        var unscoped = group.Entries.Where(x => string.IsNullOrWhiteSpace(x.RuleId) && string.IsNullOrWhiteSpace(x.RuleName)).ToList();
        if (unscoped.Count > 0)
        {
            builder.AppendLine("<details class=\"mediaCleanerTechnicalDetails\"><summary>Technical safety details</summary><ol class=\"mediaCleanerDecisionList\">");
            foreach (var entry in unscoped)
            {
                AppendTechnicalEntryHtml(builder, entry);
            }

            builder.AppendLine("</ol></details>");
        }

        builder.AppendLine("</details>");
    }

    public static void AppendItemMarkdown(
        StringBuilder builder,
        ItemDecisionGroup group,
        IReadOnlyDictionary<string, string> aliases)
    {
        builder.AppendLine();
        builder.AppendLine("<details>");
        builder.AppendLine($"<summary>{Markdown($"{group.ItemKind}: {group.ItemName} ({group.ItemId}) - {GetResultLabel(group.FinalOutcome)}")}</summary>");
        builder.AppendLine();
        builder.AppendLine($"**Result:** {Markdown(GetResultDescription(group))}");
        builder.AppendLine($"**Last stage:** {Markdown(GetLastStageSummary(group))}");

        foreach (var ruleGroup in group.RuleGroups)
        {
            builder.AppendLine();
            builder.AppendLine($"#### {Markdown(ruleGroup.RuleName)} ({GetActionLabel(ruleGroup.Action)})");
            builder.AppendLine($"- Rule result: {(ruleGroup.Matched ? "matched" : "did not match")}");
            AppendRuleSnapshotMarkdown(builder, ruleGroup.Rule, aliases);
            AppendEvidenceMarkdown(builder, ruleGroup, aliases);
            builder.AppendLine("- Technical stages:");
            foreach (var entry in ruleGroup.Entries)
            {
                builder.AppendLine($"  - {Markdown($"{entry.Stage} -> {entry.Outcome}: {entry.Reason}")}");
            }
        }

        builder.AppendLine("</details>");
    }

    public static IReadOnlyDictionary<string, string> CreateAliases(IEnumerable<MediaUser> users) =>
        users
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Select((user, index) => (user.Id, Alias: $"User {(index + 1).ToString(CultureInfo.InvariantCulture)}"))
            .ToDictionary(x => x.Id, x => x.Alias, StringComparer.OrdinalIgnoreCase);

    public static string GetResultLabel(ItemReportOutcome outcome) => outcome switch
    {
        ItemReportOutcome.Planned => "Planned",
        ItemReportOutcome.Suppressed => "Suppressed",
        ItemReportOutcome.Blocked => "Blocked",
        _ => "No action",
    };

    private static ItemReportOutcome GetFinalOutcome(IReadOnlyList<CleanupAuditEntry> entries)
    {
        if (entries.Any(x => x.Outcome == CleanupAuditOutcome.Blocked))
        {
            return ItemReportOutcome.Blocked;
        }

        if (entries.Any(x => x.Outcome == CleanupAuditOutcome.Suppressed))
        {
            return ItemReportOutcome.Suppressed;
        }

        return entries.Any(x => x.Outcome == CleanupAuditOutcome.Planned)
            ? ItemReportOutcome.Planned
            : ItemReportOutcome.NoAction;
    }

    private static string GetResultDescription(ItemDecisionGroup group) => group.FinalOutcome switch
    {
        ItemReportOutcome.Planned => "Deletion is part of the dry-run plan.",
        ItemReportOutcome.Suppressed => "A cleanup rule matched, but deletion was suppressed by a protection rule.",
        ItemReportOutcome.Blocked => "Deletion was stopped by a safety policy.",
        _ when group.RuleGroups.Any(x => x.Action == CleanupRuleActionKind.Protect && x.Matched) =>
            "No cleanup rule matched. A protection rule matched and would suppress a future cleanup match.",
        _ => "No cleanup rule matched. No deletion is proposed.",
    };

    private static void AppendLastStageHtml(StringBuilder builder, ItemDecisionGroup group)
    {
        builder.Append("<p class=\"mediaCleanerDecisionLastStage\"><strong>Last stage:</strong> ");
        builder.Append(Html(GetLastStageSummary(group)));
        builder.AppendLine("</p>");
    }

    private static string GetLastStageSummary(ItemDecisionGroup group)
    {
        var entry = group.Entries[^1];
        return $"{entry.Stage} -> {entry.Outcome}: {entry.Reason}";
    }

    private static void AppendRuleHtml(
        StringBuilder builder,
        RuleDecisionGroup group,
        IReadOnlyDictionary<string, string> userNames)
    {
        builder.AppendLine("<section class=\"mediaCleanerRuleDecisionCard\">");
        builder.Append("<div class=\"mediaCleanerRuleDecisionHeader\"><strong>");
        builder.Append(Html(group.RuleName));
        builder.Append("</strong><span class=\"mediaCleanerRuleType\">");
        builder.Append(Html(GetActionLabel(group.Action)));
        builder.Append("</span><span class=\"mediaCleanerRuleMatch ");
        builder.Append(group.Matched ? "mediaCleanerRuleMatch-matched\">Matched" : "mediaCleanerRuleMatch-rejected\">Did not match");
        builder.AppendLine("</span></div>");

        AppendRuleSnapshotHtml(builder, group.Rule, userNames);
        AppendEvidenceHtml(builder, group, userNames);

        if (!string.IsNullOrWhiteSpace(group.RuleId))
        {
            builder.Append("<button type=\"button\" class=\"emby-button raised mediaCleanerViewRuleButton\" is=\"emby-button\" data-view-rule-id=\"");
            builder.Append(HttpUtility.HtmlAttributeEncode(group.RuleId));
            builder.AppendLine("\"><span class=\"material-icons launch\" aria-hidden=\"true\"></span>&nbsp;View current rule</button>");
        }

        builder.AppendLine("<details class=\"mediaCleanerTechnicalDetails\"><summary>Technical stages</summary><ol class=\"mediaCleanerDecisionList\">");
        foreach (var entry in group.Entries)
        {
            AppendTechnicalEntryHtml(builder, entry);
        }

        builder.AppendLine("</ol></details></section>");
    }

    private static void AppendRuleSnapshotHtml(
        StringBuilder builder,
        CleanupRule? rule,
        IReadOnlyDictionary<string, string> userNames)
    {
        if (rule is null)
        {
            return;
        }

        builder.AppendLine("<div class=\"mediaCleanerRuleSnapshot\">");
        AppendConditionHtml(builder, "Trigger", DescribeTrigger(rule, userNames));
        var filters = DescribeFilters(rule, userNames).ToList();
        if (filters.Count > 0)
        {
            AppendConditionHtml(builder, "Filters (AND)", string.Join("; ", filters));
        }

        builder.AppendLine("</div>");
    }

    private static void AppendConditionHtml(StringBuilder builder, string label, string value)
    {
        builder.Append("<div><span>");
        builder.Append(Html(label));
        builder.Append("</span><strong>");
        builder.Append(Html(value));
        builder.AppendLine("</strong></div>");
    }

    private static void AppendEvidenceHtml(
        StringBuilder builder,
        RuleDecisionGroup group,
        IReadOnlyDictionary<string, string> userNames)
    {
        var evidence = group.Entries.Select(x => x.Evidence).FirstOrDefault(x => x is not null);
        if (evidence is null)
        {
            return;
        }

        builder.AppendLine("<div class=\"mediaCleanerRuleEvidence\"><strong>Evidence at dry-run time</strong><ul>");
        AppendDateHtml(builder, "Date added", evidence.DateCreatedUtc);
        AppendDateHtml(builder, "Expiration cutoff", evidence.ExpirationCutoffUtc);
        if (group.Rule is { } evaluatedRule)
        {
            AppendDateHtml(builder, "Evaluated at", EvaluatedAt(evidence, evaluatedRule));
        }

        if (evidence.PlaybackHistoryStartUtc is { } start)
        {
            AppendDateHtml(builder, "Playback history starts", start);
        }

        if (group.Rule is { } rule && rule.Trigger.Kind != CleanupRuleTriggerKind.Played)
        {
            var eligibleAt = evidence.DateCreatedUtc.AddDays(rule.Trigger.Days);
            AppendDateHtml(builder, "Age threshold reached", eligibleAt);
            AppendThresholdDistanceHtml(builder, evidence, rule, eligibleAt);
        }

        foreach (var playback in evidence.RelevantPlayback)
        {
            var name = ResolveUser(playback.UserId, playback.UserName, userNames);
            builder.Append("<li><strong>");
            builder.Append(Html(name));
            builder.Append("</strong>");
            var roles = DescribeEvidenceRoles(group.Rule, playback.UserId);
            if (!string.IsNullOrEmpty(roles))
            {
                builder.Append(" <span class=\"mediaCleanerEvidenceRoles\">(");
                builder.Append(Html(roles));
                builder.Append(")</span>");
            }

            builder.Append(": ");
            builder.Append(Html(DescribePlayback(playback)));
            if (playback.LastPlayedDate is { } lastPlayed)
            {
                builder.Append("; Last played ");
                AppendTimeHtml(builder, lastPlayed);
                if (group.Rule is { Trigger.Kind: CleanupRuleTriggerKind.Played } playedRule)
                {
                    var threshold = lastPlayed.AddDays(playedRule.Trigger.Days);
                    builder.Append("; threshold ");
                    builder.Append(Html(ThresholdDistance(evidence, playedRule, threshold)));
                }
            }

            if (IsFavoriteFilterUser(group.Rule, playback.UserId) && playback.IsFavorite)
            {
                builder.Append(playback.FavoriteSource == FavoriteSourceKind.None ? "; favorite (source unavailable)" : "; favorite via ");
                if (playback.FavoriteSource != FavoriteSourceKind.None)
                {
                    builder.Append(Html(playback.FavoriteSource.ToString().ToLowerInvariant()));
                }
            }
            else if (IsFavoriteFilterUser(group.Rule, playback.UserId))
            {
                builder.Append("; not favorite");
            }

            builder.AppendLine("</li>");
        }

        builder.AppendLine("</ul></div>");
    }

    private static void AppendDateHtml(StringBuilder builder, string label, DateTime value)
    {
        builder.Append("<li>");
        builder.Append(Html(label));
        builder.Append(": ");
        AppendTimeHtml(builder, value);
        builder.AppendLine("</li>");
    }

    private static void AppendTimeHtml(StringBuilder builder, DateTime value)
    {
        var utc = value.ToUniversalTime();
        builder.Append("<time data-media-cleaner-utc=\"");
        builder.Append(HttpUtility.HtmlAttributeEncode(utc.ToString("O", CultureInfo.InvariantCulture)));
        builder.Append("\">");
        builder.Append(Html(utc.ToString("u", CultureInfo.InvariantCulture)));
        builder.Append("</time>");
    }

    private static void AppendTechnicalEntryHtml(StringBuilder builder, CleanupAuditEntry entry)
    {
        builder.Append("<li class=\"mediaCleanerDecisionEntry\"><span class=\"mediaCleanerDecisionStage\">");
        builder.Append(Html(entry.Stage.ToString()));
        builder.Append("</span><span class=\"mediaCleanerDecisionArrow\">-&gt;</span><span class=\"mediaCleanerDecisionBadge mediaCleanerDecisionBadge-");
        builder.Append(entry.Outcome.ToString().ToLowerInvariant());
        builder.Append("\">");
        builder.Append(Html(entry.Outcome.ToString()));
        builder.Append("</span><span class=\"mediaCleanerDecisionReason\">");
        builder.Append(Html(entry.Reason));
        builder.AppendLine("</span></li>");
    }

    private static void AppendResultBadge(StringBuilder builder, ItemReportOutcome outcome)
    {
        builder.Append("<span class=\"mediaCleanerDecisionBadge mediaCleanerResultBadge-");
        builder.Append(outcome.ToString().ToLowerInvariant());
        builder.Append("\">");
        builder.Append(Html(GetResultLabel(outcome)));
        builder.Append("</span>");
    }

    private static void AppendRuleSnapshotMarkdown(
        StringBuilder builder,
        CleanupRule? rule,
        IReadOnlyDictionary<string, string> aliases)
    {
        if (rule is null)
        {
            return;
        }

        builder.AppendLine($"- Trigger: {Markdown(DescribeTrigger(rule, aliases))}");
        foreach (var filter in DescribeFilters(rule, aliases))
        {
            builder.AppendLine($"- Filter (AND): {Markdown(filter)}");
        }
    }

    private static void AppendEvidenceMarkdown(
        StringBuilder builder,
        RuleDecisionGroup group,
        IReadOnlyDictionary<string, string> aliases)
    {
        var evidence = group.Entries.Select(x => x.Evidence).FirstOrDefault(x => x is not null);
        if (evidence is null)
        {
            return;
        }

        builder.AppendLine($"- Date added: {Utc(evidence.DateCreatedUtc)}");
        builder.AppendLine($"- Expiration cutoff: {Utc(evidence.ExpirationCutoffUtc)}");
        if (group.Rule is { } evaluatedRule)
        {
            builder.AppendLine($"- Evaluated at: {Utc(EvaluatedAt(evidence, evaluatedRule))}");
        }

        if (evidence.PlaybackHistoryStartUtc is { } start)
        {
            builder.AppendLine($"- Playback history starts: {Utc(start)}");
        }

        if (group.Rule is { } rule && rule.Trigger.Kind != CleanupRuleTriggerKind.Played)
        {
            var eligibleAt = evidence.DateCreatedUtc.AddDays(rule.Trigger.Days);
            builder.AppendLine($"- Age threshold reached: {Utc(eligibleAt)} ({ThresholdDistance(evidence, rule, eligibleAt)})");
        }

        foreach (var playback in evidence.RelevantPlayback)
        {
            var name = aliases.TryGetValue(playback.UserId, out var alias) ? alias : "User";
            var roles = DescribeEvidenceRoles(group.Rule, playback.UserId);
            var roleSuffix = string.IsNullOrEmpty(roles) ? string.Empty : $" ({roles})";
            var line = $"- {name}{roleSuffix}: {DescribePlayback(playback)}";
            if (playback.LastPlayedDate is { } lastPlayed)
            {
                line += $"; Last played {Utc(lastPlayed)}";
                if (group.Rule is { Trigger.Kind: CleanupRuleTriggerKind.Played } playedRule)
                {
                    line += $"; threshold {ThresholdDistance(evidence, playedRule, lastPlayed.AddDays(playedRule.Trigger.Days))}";
                }
            }

            if (IsFavoriteFilterUser(group.Rule, playback.UserId) && playback.IsFavorite)
            {
                line += playback.FavoriteSource == FavoriteSourceKind.None
                    ? "; favorite (source unavailable)"
                    : $"; favorite via {playback.FavoriteSource.ToString().ToLowerInvariant()}";
            }
            else if (IsFavoriteFilterUser(group.Rule, playback.UserId))
            {
                line += "; not favorite";
            }

            builder.AppendLine(Markdown(line));
        }
    }

    private static string DescribeTrigger(CleanupRule rule, IReadOnlyDictionary<string, string> userNames)
    {
        var trigger = rule.Trigger;
        var userConnector = trigger.Kind == CleanupRuleTriggerKind.NotPlayed
            || trigger.PlayedKeepKind == PlayedKeepKind.AllUsers
                ? " AND "
                : " OR ";
        var users = ResolveScope(rule.Filters.UserIds, rule.Filters.UsersMode, userNames, userConnector);
        return trigger.Kind switch
        {
            CleanupRuleTriggerKind.Played when trigger.PlayedKeepKind == PlayedKeepKind.AnyUserRolling =>
                $"most recent playback by {users} is at least {trigger.Days} day(s) old",
            CleanupRuleTriggerKind.Played when trigger.PlayedKeepKind == PlayedKeepKind.AllUsers =>
                $"played by every user in {users} at least {trigger.Days} day(s) ago",
            CleanupRuleTriggerKind.Played => $"played by at least one user in {users} at least {trigger.Days} day(s) ago",
            CleanupRuleTriggerKind.NotPlayed => $"added at least {trigger.Days} day(s) ago and not played by any user in {users}",
            _ => $"added at least {trigger.Days} day(s) ago regardless of playback",
        };
    }

    private static IEnumerable<string> DescribeFilters(CleanupRule rule, IReadOnlyDictionary<string, string> userNames)
    {
        var filters = rule.Filters;
        if (filters.FavoriteFilter != RuleFavoriteFilterKind.Ignore)
        {
            var connector = filters.FavoriteFilter is RuleFavoriteFilterKind.FavoriteByAllUsers
                or RuleFavoriteFilterKind.NotFavoriteByAnyUser
                    ? " AND "
                    : " OR ";
            var scope = ResolveScope(filters.FavoriteUserIds, filters.FavoriteUsersMode, userNames, connector);
            yield return $"favorite condition {filters.FavoriteFilter} for {scope}; episodes inherit favorite from season or series, seasons from series";
        }

        if (filters.Locations.Count > 0)
        {
            yield return $"locations {filters.LocationsMode}: {string.Join(", ", filters.Locations)}";
        }

        if (filters.EnableTagFilter)
        {
            yield return $"tags {filters.TagFilterMode}: {(filters.Tags.Count == 0 ? "(none)" : string.Join(", ", filters.Tags))}";
        }
    }

    private static string ResolveScope(
        IReadOnlyList<string> ids,
        UsersListMode mode,
        IReadOnlyDictionary<string, string> userNames,
        string connector)
    {
        if (ids.Count == 0)
        {
            return mode == UsersListMode.Acknowledge ? "no selected users" : "all users";
        }

        var names = ids.Select(id => ResolveConfiguredUser(id, userNames));
        var joined = string.Join(mode == UsersListMode.Acknowledge ? connector : ", ", names);
        return mode == UsersListMode.Acknowledge ? joined : $"all users except {joined}";
    }

    private static string ResolveConfiguredUser(
        string userId,
        IReadOnlyDictionary<string, string> userNames)
    {
        if (TryResolveUserName(userId, userNames, out var name))
        {
            return name;
        }

        return $"unknown or deleted user ({userId.Trim()})";
    }

    private static string ResolveUser(
        string userId,
        string? fallback,
        IReadOnlyDictionary<string, string> userNames)
    {
        if (TryResolveUserName(userId, userNames, out var name))
        {
            return name;
        }

        return fallback ?? ResolveConfiguredUser(userId, userNames);
    }

    private static bool TryResolveUserName(
        string userId,
        IReadOnlyDictionary<string, string> userNames,
        out string name)
    {
        var normalizedId = userId.Trim();
        if (userNames.TryGetValue(normalizedId, out name!))
        {
            return true;
        }

        foreach (var candidate in userNames)
        {
            if (UserIdsEqual(normalizedId, candidate.Key))
            {
                name = candidate.Value;
                return true;
            }
        }

        name = string.Empty;
        return false;
    }

    private static string DescribePlayback(PlaybackState playback)
    {
        if (!playback.HasUserData)
        {
            return "no user data";
        }

        if (playback.IsWatching)
        {
            return "currently watching";
        }

        return playback.IsPlayed ? "played" : "not played";
    }

    private static string DescribeEvidenceRoles(CleanupRule? rule, string userId)
    {
        if (rule is null)
        {
            return string.Empty;
        }

        var roles = new List<string>(2);
        if (rule.Trigger.Kind != CleanupRuleTriggerKind.AddedAge
            && IsUserInScope(userId, rule.Filters.UserIds, rule.Filters.UsersMode))
        {
            roles.Add("playback trigger");
        }

        if (IsFavoriteFilterUser(rule, userId))
        {
            roles.Add("favorite filter");
        }

        return string.Join(", ", roles);
    }

    private static bool IsFavoriteFilterUser(CleanupRule? rule, string userId) =>
        rule is { Filters.FavoriteFilter: not RuleFavoriteFilterKind.Ignore }
        && IsUserInScope(userId, rule.Filters.FavoriteUserIds, rule.Filters.FavoriteUsersMode);

    private static bool IsUserInScope(
        string userId,
        IReadOnlyList<string> selectedUserIds,
        UsersListMode mode)
    {
        var selected = selectedUserIds.Any(selectedUserId => UserIdsEqual(userId, selectedUserId));
        return mode == UsersListMode.Acknowledge ? selected : !selected;
    }

    private static bool UserIdsEqual(string left, string right)
    {
        var trimmedLeft = left.Trim();
        var trimmedRight = right.Trim();
        return string.Equals(trimmedLeft, trimmedRight, StringComparison.OrdinalIgnoreCase)
            || (Guid.TryParse(trimmedLeft, out var leftGuid)
                && Guid.TryParse(trimmedRight, out var rightGuid)
                && leftGuid == rightGuid);
    }

    private static string GetActionLabel(CleanupRuleActionKind? action) => action switch
    {
        CleanupRuleActionKind.Delete => "Cleanup rule",
        CleanupRuleActionKind.Protect => "Protection rule",
        _ => "Rule",
    };

    private static string Utc(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void AppendThresholdDistanceHtml(
        StringBuilder builder,
        CleanupAuditEvidence evidence,
        CleanupRule rule,
        DateTime threshold)
    {
        builder.Append("<li>Threshold: ");
        builder.Append(Html(ThresholdDistance(evidence, rule, threshold)));
        builder.AppendLine("</li>");
    }

    private static string ThresholdDistance(
        CleanupAuditEvidence evidence,
        CleanupRule rule,
        DateTime threshold)
    {
        var evaluatedAt = EvaluatedAt(evidence, rule);
        var distance = evaluatedAt - threshold;
        var absolute = distance.Duration();
        var text = absolute.TotalDays >= 1
            ? $"{absolute.TotalDays.ToString("F1", CultureInfo.InvariantCulture)} day(s)"
            : absolute.TotalHours >= 1
                ? $"{absolute.TotalHours.ToString("F1", CultureInfo.InvariantCulture)} hour(s)"
                : $"{absolute.TotalMinutes.ToString("F0", CultureInfo.InvariantCulture)} minute(s)";
        return distance >= TimeSpan.Zero ? $"{text} past" : $"{text} remaining";
    }

    private static DateTime EvaluatedAt(CleanupAuditEvidence evidence, CleanupRule rule) =>
        evidence.ExpirationCutoffUtc.AddDays(rule.Trigger.Days);

    private static string Html(string value) => HttpUtility.HtmlEncode(value);

    private static string Markdown(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}
