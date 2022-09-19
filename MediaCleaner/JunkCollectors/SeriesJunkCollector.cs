using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities.TV;
using MediaCleaner.Filtering;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.JunkCollectors;

internal class SeriesJunkCollector : BaseJunkCollector
{
    public SeriesJunkCollector(
        ILogger<SeriesJunkCollector> logger,
        ItemsAdapter itemsAdapter)
        : base(logger, itemsAdapter, BaseItemKind.Episode)
    {
    }
}
