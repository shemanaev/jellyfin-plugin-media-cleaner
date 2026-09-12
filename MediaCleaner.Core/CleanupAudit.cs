using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MediaCleaner.Core;

internal sealed class CleanupAuditCollector(bool enabled)
{
    private const int MaxRuleAggregateSamples = 3;
    private readonly List<CleanupAuditEntry> _entries = [];
    private readonly Dictionary<RuleAggregateKey, RuleAggregate> _ruleAggregates = [];

    public bool Enabled { get; } = enabled;

    public IReadOnlyList<CleanupAuditEntry> Entries
    {
        get
        {
            if (_ruleAggregates.Count == 0)
            {
                return _entries;
            }

            var entries = new List<CleanupAuditEntry>(_entries.Count + _ruleAggregates.Count);
            entries.AddRange(_entries);
            foreach (var (key, aggregate) in _ruleAggregates)
            {
                var itemLabel = aggregate.Count == 1 ? "item" : "items";
                var samples = string.Join(
                    ", ",
                    aggregate.Samples.Select(sample => $"{sample.ItemName} ({sample.ItemId})"));
                entries.Add(new CleanupAuditEntry(
                    null,
                    null,
                    null,
                    key.RuleId,
                    key.RuleName,
                    key.Action,
                    key.Stage,
                    key.Outcome,
                    $"{aggregate.Count} {itemLabel} {key.Reason}. Examples: {samples}"));
            }

            return entries;
        }
    }

    public void Add(CleanupAuditEntry entry)
    {
        if (Enabled)
        {
            _entries.Add(entry);
        }
    }

    public void IncrementRuleAggregate(
        CleanupRule rule,
        MediaItem item,
        CleanupAuditStage stage,
        CleanupAuditOutcome outcome,
        string reason)
    {
        if (!Enabled)
        {
            return;
        }

        var key = new RuleAggregateKey(rule.Id, rule.Name, rule.Actions.Kind, stage, outcome, reason);
        if (!_ruleAggregates.TryGetValue(key, out var aggregate))
        {
            aggregate = new RuleAggregate();
            _ruleAggregates.Add(key, aggregate);
        }

        aggregate.Count++;
        if (aggregate.Samples.Count < MaxRuleAggregateSamples)
        {
            aggregate.Samples.Add(new RuleAggregateSample(item.Id, CleanupAudit.GetItemDisplayName(item)));
        }
    }

    private readonly record struct RuleAggregateKey(
        string RuleId,
        string RuleName,
        CleanupRuleActionKind Action,
        CleanupAuditStage Stage,
        CleanupAuditOutcome Outcome,
        string Reason);

    private sealed class RuleAggregate
    {
        public int Count { get; set; }

        public List<RuleAggregateSample> Samples { get; } = [];
    }

    private readonly record struct RuleAggregateSample(string ItemId, string ItemName);
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
    public static void IncrementRuleAggregate(
        CleanupAuditCollector audit,
        CleanupRule rule,
        MediaItem item,
        CleanupAuditStage stage,
        CleanupAuditOutcome outcome,
        string reason) =>
        audit.IncrementRuleAggregate(rule, item, stage, outcome, reason);

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
        CleanupRuleActionKind? action = null,
        CleanupAuditEvidence? evidence = null)
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
            reason.GetFormattedText(),
            evidence));
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
