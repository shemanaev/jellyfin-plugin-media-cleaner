using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.JunkCollectors;

internal class VideosPlayedJunkCollector(
    ILogger<VideosPlayedJunkCollector> logger,
    ItemsAdapter itemsAdapter) : BasePlayedJunkCollector(logger, itemsAdapter, BaseItemKind.Video);

internal class VideosNotPlayedJunkCollector(
    ILogger<VideosNotPlayedJunkCollector> logger,
    ItemsAdapter itemsAdapter) : BaseNotPlayedJunkCollector(logger, itemsAdapter, BaseItemKind.Video);
