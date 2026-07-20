using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MediaCleaner.Core;

internal sealed class CleanupAuditCollector(bool enabled)
{
    private readonly List<CleanupAuditEntry> _entries = [];

    public bool Enabled { get; } = enabled;

    public IReadOnlyList<CleanupAuditEntry> Entries => _entries;

    public void Add(CleanupAuditEntry entry)
    {
        if (Enabled)
        {
            _entries.Add(entry);
        }
    }
}

[InterpolatedStringHandler]
internal ref struct AuditReasonInterpolatedStringHandler
{
    private readonly bool _enabled;
    private DefaultInterpolatedStringHandler _handler;

    public AuditReasonInterpolatedStringHandler(
        int literalLength,
        int formattedCount,
        CleanupAuditCollector audit)
    {
        _enabled = audit.Enabled;
        _handler = _enabled
            ? new DefaultInterpolatedStringHandler(literalLength, formattedCount)
            : default;
    }

    public void AppendLiteral(string value)
    {
        if (_enabled)
        {
            _handler.AppendLiteral(value);
        }
    }

    public void AppendFormatted<T>(T value)
    {
        if (_enabled)
        {
            _handler.AppendFormatted(value);
        }
    }

    public void AppendFormatted(MediaItemKind value)
    {
        if (_enabled)
        {
            _handler.AppendFormatted(value.ToString().ToLowerInvariant());
        }
    }

    public string GetFormattedText() => _handler.ToStringAndClear();
}

internal static class CleanupAudit
{
    public static void AddRule(
        CleanupAuditCollector audit,
        CleanupRule rule,
        CleanupAuditStage stage,
        CleanupAuditOutcome outcome,
        [InterpolatedStringHandlerArgument("audit")] ref AuditReasonInterpolatedStringHandler reason)
    {
        if (!audit.Enabled)
        {
            return;
        }

        audit.Add(new CleanupAuditEntry(
            null,
            null,
            null,
            rule.Id,
            rule.Name,
            rule.Actions.Kind,
            stage,
            outcome,
            reason.GetFormattedText()));
    }

    public static void AddItem(
        CleanupAuditCollector audit,
        MediaItem item,
        CleanupRule? rule,
        CleanupAuditStage stage,
        CleanupAuditOutcome outcome,
        [InterpolatedStringHandlerArgument("audit")] ref AuditReasonInterpolatedStringHandler reason,
        CleanupRuleActionKind? action = null)
    {
        if (!audit.Enabled)
        {
            return;
        }

        audit.Add(new CleanupAuditEntry(
            item.Id,
            GetItemDisplayName(item),
            item.Kind,
            rule?.Id,
            rule?.Name,
            action ?? rule?.Actions.Kind,
            stage,
            outcome,
            reason.GetFormattedText()));
    }

    public static void AddCascadeBlocked(
        CleanupAuditCollector audit,
        MediaItem item,
        [InterpolatedStringHandlerArgument("audit")] ref AuditReasonInterpolatedStringHandler reason)
    {
        if (!audit.Enabled)
        {
            return;
        }

        audit.Add(new CleanupAuditEntry(
            item.Id,
            GetItemDisplayName(item),
            item.Kind,
            null,
            null,
            CleanupRuleActionKind.Delete,
            CleanupAuditStage.DeletionCascade,
            CleanupAuditOutcome.Blocked,
            reason.GetFormattedText()));
    }

    public static string GetItemDisplayName(MediaItem item) =>
        string.IsNullOrWhiteSpace(item.FullName) ? item.Name : item.FullName;
}
