using System.Collections.Generic;
using System.Threading;
using Jellyfin.Data.Entities;

namespace MediaCleaner.JunkCollectors
{
    internal interface IJunkCollector
    {
        List<ExpiredItem> Execute(List<User> users, List<User> usersWithFavorites, CancellationToken cancellationToken);
    }
}
