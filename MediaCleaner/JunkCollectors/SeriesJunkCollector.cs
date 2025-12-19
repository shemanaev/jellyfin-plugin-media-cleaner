using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.JunkCollectors;

internal class SeriesPlayedJunkCollector(
    ILogger<SeriesPlayedJunkCollector> logger,
    ItemsAdapter itemsAdapter) : BasePlayedJunkCollector(logger, itemsAdapter, BaseItemKind.Episode);

internal class SeriesNotPlayedJunkCollector(
    ILogger<SeriesNotPlayedJunkCollector> logger,
    ItemsAdapter itemsAdapter) : BaseNotPlayedJunkCollector(logger, itemsAdapter, BaseItemKind.Episode);
