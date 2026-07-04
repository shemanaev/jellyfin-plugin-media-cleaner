using System;
using System.Collections.Generic;
using System.Threading;

namespace MediaCleaner.Adapters;

internal sealed class SnapshotListCache<TItem>(
    Func<TItem, string> getKey,
    CancellationToken cancellationToken)
{
    private readonly Dictionary<string, IReadOnlyList<TItem>> cache = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<TItem> GetOrAdd(TItem owner, Func<IReadOnlyList<TItem>> factory)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var key = getKey(owner);
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var value = factory();
        cache[key] = value;
        return value;
    }
}
