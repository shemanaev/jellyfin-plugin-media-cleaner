using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
using MediaCleaner.Filtering;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.JunkCollectors;

internal abstract class BaseJunkCollector : IJunkCollector
{
    protected readonly ILogger _logger;
    protected readonly ItemsAdapter _itemsAdapter;
    protected readonly BaseItemKind _kind;

    protected BaseJunkCollector(
        ILogger logger,
        ItemsAdapter itemsAdapter,
        BaseItemKind kind)
    {
        _logger = logger;
        _itemsAdapter = itemsAdapter;
        _kind = kind;
    }

    public virtual List<ExpiredItem> Execute(
        List<User> users,
        IEnumerable<IExpiredItemFilter> filters,
        CancellationToken cancellationToken)
    {
        var items = users
            .SelectMany(x => _itemsAdapter.GetPlayedItems(_kind, x, cancellationToken))
            .ToList();

        _logger.LogDebug("{Count} items before filtering", items.Count);

        foreach (var filter in filters)
        {
            var before = items;
            var after = filter.Apply(before);

            foreach (var item in before.Except(after))
            {
                _logger.LogTrace("'{Name}' has been filtered by {FilterName}", item.Item.Name, filter.Name);
            }

            items = after;
        }

        _logger.LogDebug("{Count} items after filtering", items.Count);
        return items;
    }
}
