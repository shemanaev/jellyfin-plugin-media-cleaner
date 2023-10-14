using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.JunkCollectors;

internal class AudioJunkCollector : BaseJunkCollector
{
    public AudioJunkCollector(
        ILogger<AudioJunkCollector> logger,
        ItemsAdapter itemsAdapter)
        : base(logger, itemsAdapter, BaseItemKind.Audio)
    {
    }
}
