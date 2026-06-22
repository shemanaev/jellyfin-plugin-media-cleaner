using System;

namespace MediaCleaner.Core;

public interface IClock
{
    DateTime UtcNow { get; }
}

public interface IPathMatcher
{
    bool ContainsSubPath(string parentPath, string path);
}

public interface IExtraFileProbe
{
    bool HasBlockingExtraFiles(MediaItem item);
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

public sealed class OrdinalPathMatcher : IPathMatcher
{
    public bool ContainsSubPath(string parentPath, string path) =>
        path.StartsWith(parentPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}

public sealed class NoExtraFileProbe : IExtraFileProbe
{
    public bool HasBlockingExtraFiles(MediaItem item) => false;
}
