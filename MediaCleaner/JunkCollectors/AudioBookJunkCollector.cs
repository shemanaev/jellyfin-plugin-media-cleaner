using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.JunkCollectors;

internal class AudioBookPlayedJunkCollector(
    ILogger<AudioBookPlayedJunkCollector> logger,
    ItemsAdapter itemsAdapter) : BasePlayedJunkCollector(logger, itemsAdapter, BaseItemKind.AudioBook);

internal class AudioBookNotPlayedJunkCollector(
    ILogger<AudioBookNotPlayedJunkCollector> logger,
    ItemsAdapter itemsAdapter) : BaseNotPlayedJunkCollector(logger, itemsAdapter, BaseItemKind.AudioBook);
