using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MediaCleaner.Core;

public static class CleanupAuditFormatter
{
    public static IReadOnlyList<CleanupAuditEntry> GetItemEntries(
        IEnumerable<CleanupAuditEntry> auditEntries,
        string itemId) =>
        auditEntries
            .Where(x => string.Equals(x.ItemId, itemId, StringComparison.OrdinalIgnoreCase))
            .ToList();

    public static string FormatPlainTextEntry(
        CleanupAuditEntry entry,
        bool includeAction = false,
        Func<string, string>? escapeText = null)
    {
        Func<string, string> escape = escapeText ?? (static value => value);
        var builder = new StringBuilder();
        builder.Append(entry.Stage);
        builder.Append(" -> ");
        builder.Append(entry.Outcome);
        if (!string.IsNullOrWhiteSpace(entry.RuleName))
        {
            builder.Append(" [");
            builder.Append(escape(entry.RuleName));
            builder.Append(']');
        }

        if (includeAction && entry.Action is not null)
        {
            builder.Append(" (");
            builder.Append(entry.Action);
            builder.Append(')');
        }

        builder.Append(": ");
        builder.Append(escape(entry.Reason));
        return builder.ToString();
    }

    public static CleanupAuditOutcome GetFinalOutcome(IEnumerable<CleanupAuditEntry> entries)
    {
        var outcomes = entries.Select(x => x.Outcome).ToList();
        if (outcomes.Contains(CleanupAuditOutcome.Blocked))
        {
            return CleanupAuditOutcome.Blocked;
        }

        if (outcomes.Contains(CleanupAuditOutcome.Suppressed))
        {
            return CleanupAuditOutcome.Suppressed;
        }

        if (outcomes.Contains(CleanupAuditOutcome.Planned))
        {
            return CleanupAuditOutcome.Planned;
        }

        if (outcomes.Contains(CleanupAuditOutcome.Protected))
        {
            return CleanupAuditOutcome.Protected;
        }

        if (outcomes.Contains(CleanupAuditOutcome.Rejected))
        {
            return CleanupAuditOutcome.Rejected;
        }

        if (outcomes.Contains(CleanupAuditOutcome.Skipped))
        {
            return CleanupAuditOutcome.Skipped;
        }

        return CleanupAuditOutcome.Matched;
    }
}
