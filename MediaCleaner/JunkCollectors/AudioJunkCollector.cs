using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.JunkCollectors;

internal class AudioPlayedJunkCollector(
    ILogger<AudioPlayedJunkCollector> logger,
    ItemsAdapter itemsAdapter) : BasePlayedJunkCollector(logger, itemsAdapter, BaseItemKind.Audio);

internal class AudioNotPlayedJunkCollector(
    ILogger<AudioNotPlayedJunkCollector> logger,
    ItemsAdapter itemsAdapter) : BaseNotPlayedJunkCollector(logger, itemsAdapter, BaseItemKind.Audio);
