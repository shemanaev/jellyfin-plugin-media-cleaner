using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.JunkCollectors;

internal class MoviesJunkCollector : BaseJunkCollector
{
    public MoviesJunkCollector(
        ILogger<MoviesJunkCollector> logger,
        ItemsAdapter itemsAdapter)
        : base(logger, itemsAdapter, BaseItemKind.Movie)
    {
    }
}
