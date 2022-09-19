using System.Collections.Generic;
using System.Threading;
using Jellyfin.Data.Entities;
using MediaCleaner.Filtering;

namespace MediaCleaner.JunkCollectors
{
    internal interface IJunkCollector
    {
        List<ExpiredItem> Execute(List<User> users, IEnumerable<IExpiredItemFilter> filters, CancellationToken cancellationToken);
    }
}
