using System;
using System.Collections.Generic;
using System.Linq;

namespace MediaCleaner.Core;

public sealed class CleanupPlanner(IClock clock, IPathMatcher pathMatcher, IExtraFileProbe extraFileProbe)
{
    public CleanupPlan Plan(CleanupRequest request)
    {
        var enabledRules = request.Policy.Rules.Where(x => x.Enabled).ToList();
        if (enabledRules.Count == 0)
        {
            return CleanupPlan.Empty;
        }

        var now = clock.UtcNow;
        var audit = new CleanupAuditCollector(request.IsDryRun);
        var catalog = CleanupCatalogIndex.Create(request.Items);
        var matcher = new CleanupRuleMatcher(now, pathMatcher, request.Policy);
        var deleteMatches = request.IsDryRun ? new List<RuleMatch>() : null;
        var protectMatches = request.IsDryRun ? new List<RuleMatch>() : null;
        var decisionAccumulator = request.IsDryRun ? null : new DeleteDecisionAccumulator();
        var watchingIds = catalog.ItemsById.Values
            .Where(item => item.Playback.Any(playback => playback.IsWatching))
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var externalProtectedIds = (request.ExternalProtectedItemIds ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var protectedIds = new HashSet<string>(watchingIds, StringComparer.OrdinalIgnoreCase);
        protectedIds.UnionWith(externalProtectedIds);

        if (request.IsDryRun)
        {
            foreach (var item in catalog.ItemsById.Values.Where(item => watchingIds.Contains(item.Id)))
            {
                CleanupAudit.AddItem(
                    audit,
                    item,
                    null,
                    CleanupAuditStage.Protection,
                    CleanupAuditOutcome.Protected,
                    $"protected because the item is currently being watched");
            }

            foreach (var item in catalog.ItemsById.Values.Where(item => externalProtectedIds.Contains(item.Id)))
            {
                var reasons = request.ExternalProtectionReasons?.GetValueOrDefault(item.Id) ?? ["protected by an external safety exclusion"];
                foreach (var reason in reasons)
                {
                    CleanupAudit.AddItem(
                        audit,
                        item,
                        null,
                        CleanupAuditStage.ExternalProtection,
                        CleanupAuditOutcome.Protected,
                        $"{reason}");
                }
            }
        }

        foreach (var rule in enabledRules)
        {
            var context = matcher.CreateContext(request.Users, rule, audit);
            if (context is null)
            {
                continue;
            }

            foreach (var match in matcher.CollectRuleMatches(catalog, context, audit))
            {
                if (rule.Actions.Kind == CleanupRuleActionKind.Delete)
                {
                    if (request.IsDryRun)
                    {
                        deleteMatches!.Add(match);
                    }
                    else
                    {
                        decisionAccumulator!.Add(match);
                    }
                }
                else if (rule.Actions.Kind == CleanupRuleActionKind.Protect)
                {
                    protectedIds.Add(match.Item.Id);
                    if (request.IsDryRun)
                    {
                        protectMatches!.Add(match);
                    }
                }
                else
                {
                    throw new NotSupportedException($"Unsupported rule action: {rule.Actions.Kind}");
                }
            }
        }

        foreach (var protectedMatch in protectMatches ?? [])
        {
            CleanupAudit.AddItem(
                audit,
                protectedMatch.Item,
                protectedMatch.Rule,
                CleanupAuditStage.Protection,
                CleanupAuditOutcome.Protected,
                $"protected by rule '{protectedMatch.Rule.Name}'");
        }

        var decisions = (request.IsDryRun
                ? BuildDeleteDecisions(
                    deleteMatches!,
                    protectedIds,
                    watchingIds,
                    externalProtectedIds,
                    request.ExternalProtectionReasons,
                    protectMatches!,
                    audit)
                : decisionAccumulator!.BuildDecisions(protectedIds))
            .OrderBy(x => CleanupRuleKinds.Priority(x.Kind))
            .ThenBy(x => x.Kind == ExpiredKind.Played ? FirstPlaybackLastPlayedDate(x.Playback) : x.Item.DateCreated)
            .ToList();

        var cascadePlanner = new DeletionCascadePlanner(extraFileProbe);
        IReadOnlyList<DeletionOperation> deletions;
        if (request.IsDryRun)
        {
            // Dry-run needs the deletion-cascade audit entries and counts, but it does not need
            // to retain every DeletionOperation object. On large not-played libraries this avoids
            // keeping tens of thousands of deletion records alive until the report is rendered.
            foreach (var _ in cascadePlanner.BuildDeletionOperations(decisions, catalog.ItemsById, protectedIds, audit))
            {
            }

            deletions = [];
        }
        else
        {
            deletions = cascadePlanner.BuildDeletionOperations(decisions, catalog.ItemsById, protectedIds, audit).ToList();
        }

        return new CleanupPlan(decisions, deletions, audit.Entries);
    }

    public static IEnumerable<MediaUser> FilterUsers(
        IEnumerable<MediaUser> users,
        IReadOnlyCollection<string> selectedUserIds,
        UsersListMode mode) =>
        users.Where(user => selectedUserIds.Contains(user.Id) switch
        {
            true when mode == UsersListMode.Ignore => false,
            true when mode == UsersListMode.Acknowledge => true,
            false when mode == UsersListMode.Ignore => true,
            false when mode == UsersListMode.Acknowledge => false,
            _ => throw new NotSupportedException($"Unsupported users list mode: {mode}"),
        });

    private static DateTime? FirstPlaybackLastPlayedDate(IReadOnlyList<PlaybackState> playback)
    {
        return playback[0].LastPlayedDate;
    }

    private static IEnumerable<CleanupDecision> BuildDeleteDecisions(
        IEnumerable<RuleMatch> deleteMatches,
        ISet<string> protectedIds,
        ISet<string> watchingIds,
        ISet<string> externalProtectedIds,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? externalProtectionReasons,
        IReadOnlyCollection<RuleMatch> protectMatches,
        CleanupAuditCollector audit)
    {
        foreach (var group in deleteMatches.GroupBy(x => x.Item.Id, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            if (protectedIds.Contains(first.Item.Id))
            {
                var suppressionReason = BuildSuppressionReason(
                    first.Item.Id,
                    watchingIds,
                    externalProtectedIds,
                    externalProtectionReasons,
                    protectMatches);
                foreach (var match in group)
                {
                    CleanupAudit.AddItem(
                        audit,
                        match.Item,
                        match.Rule,
                        CleanupAuditStage.Protection,
                        CleanupAuditOutcome.Suppressed,
                        $"{suppressionReason}");
                }

                continue;
            }

            var selectedKind = group
                .Select(x => x.Kind)
                .OrderBy(CleanupRuleKinds.Priority)
                .First();
            var selectedItem = group
                .Where(x => x.Kind == selectedKind)
                .Select(x => x.Item)
                .First();
            var playback = group
                .SelectMany(x => x.Playback)
                .GroupBy(x => x.UserId, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.OrderByDescending(y => y.LastPlayedDate).First())
                .ToList();
            var matchedRules = group
                .Select(x => x.Rule)
                .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Name)
                .ToList();
            var matchedRuleIds = group
                .Select(x => x.Rule)
                .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Id)
                .ToList();
            var markUnplayedUsers = group
                .Where(x => x.Kind == ExpiredKind.Played && x.Rule.Actions.MarkAsUnplayed)
                .SelectMany(x => x.Playback.Select(y => y.UserId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            yield return CleanupDecisionFactory.Create(selectedItem, selectedKind, playback, markUnplayedUsers, matchedRules, matchedRuleIds);
        }
    }

    private static string BuildSuppressionReason(
        string itemId,
        ISet<string> watchingIds,
        ISet<string> externalProtectedIds,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? externalProtectionReasons,
        IReadOnlyCollection<RuleMatch> protectMatches)
    {
        var reasons = new List<string>();
        if (watchingIds.Contains(itemId))
        {
            reasons.Add("item is currently being watched");
        }

        if (externalProtectedIds.Contains(itemId))
        {
            reasons.AddRange(
                externalProtectionReasons?.GetValueOrDefault(itemId)
                ?? ["protected by an external safety exclusion"]);
        }

        reasons.AddRange(protectMatches
            .Where(x => string.Equals(x.Item.Id, itemId, StringComparison.OrdinalIgnoreCase))
            .Select(x => $"matched protection rule '{x.Rule.Name}'")
            .Distinct(StringComparer.OrdinalIgnoreCase));

        return reasons.Count == 0
            ? "delete suppressed because item is protected"
            : $"delete suppressed because {string.Join("; ", reasons)}";
    }
}
