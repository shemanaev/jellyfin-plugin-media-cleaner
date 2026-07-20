using System;
using System.Collections.Generic;
using System.Linq;

namespace MediaCleaner.Core;

internal sealed record CandidateItem(MediaItem Item, IReadOnlyList<PlaybackState> Playback);

internal sealed record RuleMatch(CleanupRule Rule, MediaItem Item, ExpiredKind Kind, IReadOnlyList<PlaybackState> Playback);

internal sealed record RuleEvaluationContext(
    CleanupRule Rule,
    IReadOnlyList<MediaUser> Users,
    ISet<string> UserIds,
    IReadOnlyList<MediaUser> FavoriteUsers,
    ISet<MediaItemKind> MediaKinds,
    DateTime? PlaybackStartDate);

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
