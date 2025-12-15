using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Data.Enums;
using MediaCleaner.Models;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.JunkCollectors;

internal abstract class BasePlayedJunkCollector(
    ILogger logger,
    ItemsAdapter itemsAdapter,
    BaseItemKind kind) : IJunkCollector
{
    public virtual List<ExpiredItem> Execute(
        List<User> users,
        DateTime? startDate,
        CancellationToken cancellationToken)
    {
        logger.LogTrace("Collecting played items for {UsersCount} users started at {StartTime}", users.Count, DateTime.Now);
        var items = users
            .SelectMany(x => itemsAdapter.GetPlayedItems(kind, x, startDate, cancellationToken))
            .GroupBy(x => x.Item)
            .Select(x => new ExpiredItem
            {
                Item = x.Key,
                Reason = x.Select(a => a.Reason).FirstOrDefault(),
                Data = x.SelectMany(a => a.Data.Select(z => z))
                    .OrderByDescending(a => a.LastPlayedDate)
                    .ToList()
            })
            .ToList();

        logger.LogDebug("{Count} items collected", items.Count);
        logger.LogTrace("Collecting finished at {StartTime}", DateTime.Now);

        return items;
    }
}
