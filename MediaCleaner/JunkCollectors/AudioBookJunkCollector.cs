using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.JunkCollectors;

internal class AudioBookJunkCollector : BaseJunkCollector
{
    public AudioBookJunkCollector(
        ILogger<AudioBookJunkCollector> logger,
        ItemsAdapter itemsAdapter)
        : base(logger, itemsAdapter, BaseItemKind.AudioBook)
    {
    }
}
