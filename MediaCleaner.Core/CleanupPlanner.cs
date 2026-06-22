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
        var auditEntries = new List<CleanupAuditEntry>();
        var matcher = new CleanupRuleMatcher(now, pathMatcher, request.Policy);
        var deleteMatches = new List<RuleMatch>();
        var protectMatches = new List<RuleMatch>();

        foreach (var rule in enabledRules)
        {
            var matches = matcher.CollectRuleMatches(request, rule, auditEntries).ToList();
            if (rule.Actions.Kind == CleanupRuleActionKind.Delete)
            {
                deleteMatches.AddRange(matches);
            }
            else if (rule.Actions.Kind == CleanupRuleActionKind.Protect)
            {
                protectMatches.AddRange(matches);
            }
            else
            {
                throw new NotSupportedException($"Unsupported rule action: {rule.Actions.Kind}");
            }
        }

        var protectedIds = protectMatches.Select(x => x.Item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var protectedMatch in protectMatches)
        {
            CleanupAudit.AddItem(
                auditEntries,
                protectedMatch.Item,
                protectedMatch.Rule,
                CleanupAuditStage.Protection,
                CleanupAuditOutcome.Protected,
                $"protected by rule '{protectedMatch.Rule.Name}'");
        }

        var decisions = BuildDeleteDecisions(deleteMatches, protectedIds, auditEntries)
            .OrderBy(x => CleanupRuleKinds.Priority(x.Kind))
            .ThenBy(x => x.Kind == ExpiredKind.Played ? x.Playback.FirstOrDefault()?.LastPlayedDate : x.Item.DateCreated)
            .ToList();

        var cascadePlanner = new DeletionCascadePlanner(extraFileProbe);
        var plannedDeletions = cascadePlanner.BuildDeletionOperations(decisions, request.Items, protectedIds, auditEntries).ToList();
        var deletions = request.IsDryRun ? [] : plannedDeletions;

        return new CleanupPlan(decisions, deletions, auditEntries);
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

    private static IEnumerable<CleanupDecision> BuildDeleteDecisions(
        IEnumerable<RuleMatch> deleteMatches,
        ISet<string> protectedIds,
        List<CleanupAuditEntry> auditEntries)
    {
        foreach (var group in deleteMatches.GroupBy(x => x.Item.Id, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            if (protectedIds.Contains(first.Item.Id))
            {
                foreach (var match in group)
                {
                    CleanupAudit.AddItem(
                        auditEntries,
                        match.Item,
                        match.Rule,
                        CleanupAuditStage.Protection,
                        CleanupAuditOutcome.Suppressed,
                        "delete suppressed because item is protected");
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
            var markUnplayedUsers = group
                .Where(x => x.Kind == ExpiredKind.Played && x.Rule.Actions.MarkAsUnplayed)
                .SelectMany(x => x.Playback.Select(y => y.UserId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            yield return CleanupDecisionFactory.Create(selectedItem, selectedKind, playback, markUnplayedUsers, matchedRules);
        }
    }
}
