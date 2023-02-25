using Jellyfin.Data.Enums;
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
