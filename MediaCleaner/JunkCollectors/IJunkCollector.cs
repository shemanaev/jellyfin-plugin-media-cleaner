using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using MediaCleaner.Filtering;

namespace MediaCleaner.JunkCollectors
{
    internal interface IJunkCollector
    {
        List<ExpiredItem> Execute(List<User> users, IEnumerable<IExpiredItemFilter> filters, DateTime? startDate, CancellationToken cancellationToken);
        List<ExpiredItem> ExecuteNotPlayed(List<User> users, IEnumerable<IExpiredItemFilter> filters, DateTime? startDate, CancellationToken cancellationToken);
    }
}
