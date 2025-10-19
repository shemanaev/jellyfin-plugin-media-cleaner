using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
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
        DateTime? startDate,
        CancellationToken cancellationToken)
    {
        _logger.LogTrace("Collecting played items for {UsersCount} users started at {StartTime}", users.Count, DateTime.Now);
        var items = users
            .SelectMany(x => _itemsAdapter.GetPlayedItems(_kind, x, startDate, cancellationToken))
            .GroupBy(x => x.Item)
            .Select(x => new ExpiredItem
            {
                Item = x.Key,
                Kind = x.Select(a => a.Kind).FirstOrDefault(),
                Data = x.SelectMany(a => a.Data.Select(z => z))
                        .OrderByDescending(a => a.LastPlayedDate)
                        .ToList()
            })
            .ToList();

        _logger.LogDebug("Filters order: {Filters}", string.Join(", ", filters.Select(x => x.Name)));
        _logger.LogDebug("{Count} items before filtering", items.Count);

        foreach (var filter in filters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = items;
            var after = filter.Apply(before);

            foreach (var item in before.Except(after))
            {
                _logger.LogTrace("\"{Name}\" filtered by {FilterName}", item.Item.Name, filter.Name);
            }

            items = after;
        }

        _logger.LogDebug("{Count} items after filtering", items.Count);
        _logger.LogTrace("Collecting finished at {StartTime}", DateTime.Now);
        return items;
    }

    public virtual List<ExpiredItem> ExecuteNotPlayed(
        List<User> users,
        IEnumerable<IExpiredItemFilter> filters,
        DateTime? startDate,
        CancellationToken cancellationToken)
    {
        _logger.LogTrace("Collecting not played items started at {StartTime}", DateTime.Now);
        var items = users
            .SelectMany(x => _itemsAdapter.GetNotPlayedItems(_kind, x, startDate, cancellationToken))
            .GroupBy(x => x.Item)
            .Where(x => x.Count() == users.Count)
            .Select(x => new ExpiredItem
            {
                Item = x.Key,
                Kind = x.Select(a => a.Kind).FirstOrDefault(),
            })
            .ToList();

        _logger.LogDebug("Filters order: {Filters}", string.Join(", ", filters.Select(x => x.Name)));
        _logger.LogDebug("{Count} items before filtering", items.Count);

        foreach (var filter in filters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = items;
            var after = filter.Apply(before);

            foreach (var item in before.Except(after))
            {
                _logger.LogTrace("\"{Name}\" filtered by {FilterName}", item.Item.Name, filter.Name);
            }

            items = after;
        }

        _logger.LogDebug("{Count} items after filtering", items.Count);
        _logger.LogTrace("Collecting finished at {StartTime}", DateTime.Now);
        return items;
    }
}
