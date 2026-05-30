using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using MediaCleaner.Models;

namespace MediaCleaner.JunkCollectors;

internal interface IJunkCollector
{
    List<ExpiredItem> Execute(List<User> users, DateTime? startDate, CancellationToken cancellationToken);
}
