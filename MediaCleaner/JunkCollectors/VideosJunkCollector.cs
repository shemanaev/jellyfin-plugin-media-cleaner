using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.JunkCollectors;

internal class VideosJunkCollector : BaseJunkCollector
{
    public VideosJunkCollector(
        ILogger<VideosJunkCollector> logger,
        ItemsAdapter itemsAdapter)
        : base(logger, itemsAdapter, BaseItemKind.Video)
    {
    }
}
