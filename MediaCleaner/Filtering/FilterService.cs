using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MediaCleaner.Models;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.Filtering;

internal class FilterService(ILogger logger)
{
    public List<ExpiredItem> Execute(
        List<ExpiredItem> items,
        List<IExpiredItemFilter> filters,
        CancellationToken cancellationToken)
    {
        logger.LogDebug("Filters order: {Filters}", string.Join(", ", filters.Select(x => x.Name)));
        logger.LogDebug("{Count} items before filtering", items.Count);

        foreach (var filter in filters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = items;
            var after = filter.Apply(before);

            foreach (var item in before.Except(after))
            {
                logger.LogTrace("\"{Name}\" filtered by {FilterName}", item.Item.Name, filter.Name);
            }

            items = after;
        }

        logger.LogDebug("{Count} items after filtering", items.Count);

        return items;
    }
}
