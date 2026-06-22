using System.IO;
using System.Linq;
using MediaCleaner.Core;

namespace MediaCleaner.Adapters;

internal sealed class JellyfinExtraFileProbe : IExtraFileProbe
{
    public bool HasBlockingExtraFiles(MediaItem item)
    {
        if (string.IsNullOrEmpty(item.Path) || !Directory.Exists(item.Path))
        {
            return false;
        }

        return Directory.EnumerateFiles(item.Path, ".ytdl-sub-*-download-archive.json").Any();
    }
}
