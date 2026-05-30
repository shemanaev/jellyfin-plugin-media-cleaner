using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.JunkCollectors;

internal class MoviesPlayedJunkCollector(
    ILogger<MoviesPlayedJunkCollector> logger,
    ItemsAdapter itemsAdapter) : BasePlayedJunkCollector(logger, itemsAdapter, BaseItemKind.Movie);

internal class MoviesNotPlayedJunkCollector(
    ILogger<MoviesNotPlayedJunkCollector> logger,
    ItemsAdapter itemsAdapter) : BaseNotPlayedJunkCollector(logger, itemsAdapter, BaseItemKind.Movie);
