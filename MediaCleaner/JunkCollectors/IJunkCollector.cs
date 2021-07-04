using System.Collections.Generic;
using System.Threading;
using Jellyfin.Data.Entities;

namespace MediaCleaner.JunkCollectors
{
    internal interface IJunkCollector
    {
        List<ExpiredItem> Execute(List<User> users, CancellationToken cancellationToken);
    }
}
