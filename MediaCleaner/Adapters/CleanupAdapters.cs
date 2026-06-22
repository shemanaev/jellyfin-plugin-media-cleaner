using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaCleaner.Core;

namespace MediaCleaner.Adapters;

internal sealed record CleanupCatalog(
    IReadOnlyList<MediaUser> Users,
    IReadOnlyList<MediaItem> Items,
    IReadOnlyDictionary<string, BaseItem> ItemsById,
    IReadOnlyDictionary<string, JellyfinUser> UsersById);

internal interface ICleanupPolicyProvider
{
    CleanupPolicy GetPolicy();
    bool RequiresMigrationReview { get; }
}

internal interface IMediaCatalogAdapter
{
    CleanupCatalog Create(CleanupPolicy policy, CancellationToken cancellationToken);
}

internal interface IMediaMutationAdapter
{
    Task ExecuteAsync(CleanupPlan plan, CleanupCatalog catalog, CancellationToken cancellationToken);
}
