using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Data.Enums;
using MediaCleaner.Models;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.JunkCollectors;

internal abstract class BaseNotPlayedJunkCollector(
    ILogger logger,
    ItemsAdapter itemsAdapter,
    BaseItemKind kind) : IJunkCollector
{
    public virtual List<ExpiredItem> Execute(
        List<User> users,
        DateTime? startDate,
        CancellationToken cancellationToken)
    {
        logger.LogTrace("Collecting not played items started at {StartTime}", DateTime.Now);
        var items = users
            .SelectMany(x => itemsAdapter.GetNotPlayedItems(kind, x, startDate, cancellationToken))
            .GroupBy(x => x.Item)
            .Where(x => x.Count() == users.Count)
            .Select(x => new ExpiredItem { Item = x.Key, Reason = x.Select(a => a.Reason).FirstOrDefault(), })
            .ToList();


        logger.LogDebug("{Count} items collected", items.Count);
        logger.LogTrace("Collecting finished at {StartTime}", DateTime.Now);

        return items;
    }
}
