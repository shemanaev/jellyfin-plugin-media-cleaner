using System.Collections.Generic;

namespace MediaCleaner.Core;

internal static class CleanupAudit
{
    public static void AddRule(
        List<CleanupAuditEntry> auditEntries,
        CleanupRule rule,
        CleanupAuditStage stage,
        CleanupAuditOutcome outcome,
        string reason)
    {
        auditEntries.Add(new CleanupAuditEntry(
            null,
            null,
            null,
            rule.Id,
            rule.Name,
            rule.Actions.Kind,
            stage,
            outcome,
            reason));
    }

    public static void AddItem(
        List<CleanupAuditEntry> auditEntries,
        MediaItem item,
        CleanupRule? rule,
        CleanupAuditStage stage,
        CleanupAuditOutcome outcome,
        string reason,
        CleanupRuleActionKind? action = null)
    {
        auditEntries.Add(new CleanupAuditEntry(
            item.Id,
            GetItemDisplayName(item),
            item.Kind,
            rule?.Id,
            rule?.Name,
            action ?? rule?.Actions.Kind,
            stage,
            outcome,
            reason));
    }

    public static void AddCascadeBlocked(List<CleanupAuditEntry> auditEntries, MediaItem item, string reason) =>
        AddItem(
            auditEntries,
            item,
            null,
            CleanupAuditStage.DeletionCascade,
            CleanupAuditOutcome.Blocked,
            reason,
            CleanupRuleActionKind.Delete);

    public static string GetItemDisplayName(MediaItem item) =>
        string.IsNullOrWhiteSpace(item.FullName) ? item.Name : item.FullName;
}
