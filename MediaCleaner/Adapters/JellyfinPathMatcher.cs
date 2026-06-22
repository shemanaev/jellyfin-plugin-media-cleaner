using MediaBrowser.Model.IO;
using MediaCleaner.Core;

namespace MediaCleaner.Adapters;

internal sealed class JellyfinPathMatcher(IFileSystem fileSystem) : IPathMatcher
{
    public bool ContainsSubPath(string parentPath, string path) =>
        fileSystem.ContainsSubPath(parentPath, path);
}
