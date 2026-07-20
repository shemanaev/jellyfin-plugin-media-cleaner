using System;
using System.Collections.Generic;
using System.Linq;

namespace MediaCleaner.Core;

internal sealed record CandidateItem(MediaItem Item, IReadOnlyList<PlaybackState> Playback);

internal sealed record RuleMatch(CleanupRule Rule, MediaItem Item, ExpiredKind Kind, IReadOnlyList<PlaybackState> Playback);

internal sealed class DeleteDecisionAccumulator
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Entry> _orderedEntries = [];

    public void Add(RuleMatch match)
    {
        if (!_entries.TryGetValue(match.Item.Id, out var entry))
        {
            entry = new Entry(match.Item, match.Kind);
            _entries.Add(match.Item.Id, entry);
            _orderedEntries.Add(entry);
        }
        else if (CleanupRuleKinds.Priority(match.Kind) < CleanupRuleKinds.Priority(entry.Kind))
        {
            entry.Item = match.Item;
            entry.Kind = match.Kind;
        }

        if (!entry.Rules.ContainsKey(match.Rule.Id))
        {
            entry.Rules.Add(match.Rule.Id, match.Rule);
        }

        foreach (var state in match.Playback)
        {
            if (!entry.PlaybackByUser.TryGetValue(state.UserId, out var existing))
            {
                entry.PlaybackByUser.Add(state.UserId, state);
                entry.PlaybackUserOrder.Add(state.UserId);
            }
            else if (state.LastPlayedDate > existing.LastPlayedDate)
            {
                entry.PlaybackByUser[state.UserId] = state;
            }

            if (match.Kind == ExpiredKind.Played
                && match.Rule.Actions.MarkAsUnplayed
                && entry.MarkUnplayedUsers.Add(state.UserId))
            {
                entry.MarkUnplayedUserOrder.Add(state.UserId);
            }
        }
    }

    public IEnumerable<CleanupDecision> BuildDecisions(ISet<string> protectedIds)
    {
        foreach (var entry in _orderedEntries)
        {
            if (protectedIds.Contains(entry.Item.Id))
            {
                continue;
            }

            var playback = new List<PlaybackState>(entry.PlaybackUserOrder.Count);
            foreach (var userId in entry.PlaybackUserOrder)
            {
                playback.Add(entry.PlaybackByUser[userId]);
            }

            var matchedRules = entry.Rules.Values
                .OrderBy(rule => rule.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(rule => rule.Id, StringComparer.OrdinalIgnoreCase)
                .Select(rule => rule.Name)
                .ToList();
            yield return CleanupDecisionFactory.Create(
                entry.Item,
                entry.Kind,
                playback,
                entry.MarkUnplayedUserOrder,
                matchedRules);
        }
    }

    private sealed class Entry(MediaItem item, ExpiredKind kind)
    {
        public MediaItem Item { get; set; } = item;

        public ExpiredKind Kind { get; set; } = kind;

        public Dictionary<string, CleanupRule> Rules { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, PlaybackState> PlaybackByUser { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> PlaybackUserOrder { get; } = [];

        public HashSet<string> MarkUnplayedUsers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> MarkUnplayedUserOrder { get; } = [];
    }
}

internal sealed record RuleEvaluationContext(
    CleanupRule Rule,
    IReadOnlyList<MediaUser> Users,
    ISet<string> UserIds,
    IReadOnlyList<MediaUser> FavoriteUsers,
    ISet<MediaItemKind> MediaKinds,
    DateTime? PlaybackStartDate,
    DateTime ExpirationCutoffDate);

internal sealed class CleanupCatalogIndex
{
    private readonly IReadOnlyList<MediaItem> _items;
    private readonly IReadOnlyDictionary<MediaItemKind, IReadOnlyList<MediaItem>> _itemsByKind;

    private CleanupCatalogIndex(
        IReadOnlyList<MediaItem> items,
        IReadOnlyDictionary<string, MediaItem> itemsById,
        IReadOnlyDictionary<MediaItemKind, IReadOnlyList<MediaItem>> itemsByKind)
    {
        _items = items;
        ItemsById = itemsById;
        _itemsByKind = itemsByKind;
    }

    public IReadOnlyDictionary<string, MediaItem> ItemsById { get; }

    public static CleanupCatalogIndex Create(IReadOnlyList<MediaItem> items)
    {
        var itemsById = new Dictionary<string, MediaItem>(items.Count, StringComparer.OrdinalIgnoreCase);
        var mutableItemsByKind = new Dictionary<MediaItemKind, List<MediaItem>>();

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            itemsById.Add(item.Id, item);
            if (!mutableItemsByKind.TryGetValue(item.Kind, out var bucket))
            {
                bucket = [];
                mutableItemsByKind.Add(item.Kind, bucket);
            }

            bucket.Add(item);
        }

        return new CleanupCatalogIndex(
            items,
            itemsById,
            mutableItemsByKind.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<MediaItem>)pair.Value));
    }

    public IEnumerable<MediaItem> GetRuleItems(ISet<MediaItemKind> mediaKinds)
    {
        if (mediaKinds.Count == 1)
        {
            var kind = mediaKinds.First();
            return _itemsByKind.TryGetValue(kind, out var bucket) ? bucket : [];
        }

        return _items.Where(item => mediaKinds.Contains(item.Kind));
    }
}
