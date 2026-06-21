using System;
using System.Collections.Generic;
using System.Threading;
using MediaCleaner.Filtering;

namespace MediaCleaner.JunkCollectors
{
    internal interface IJunkCollector
    {
        List<ExpiredItem> Execute(List<JellyfinUser> users, IEnumerable<IExpiredItemFilter> filters, DateTime? startDate, CancellationToken cancellationToken);
        List<ExpiredItem> ExecuteNotPlayed(List<JellyfinUser> users, IEnumerable<IExpiredItemFilter> filters, DateTime? startDate, CancellationToken cancellationToken);
    }
}
