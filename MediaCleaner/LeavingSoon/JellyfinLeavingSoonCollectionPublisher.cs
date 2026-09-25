using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaCleaner.Adapters;
using MediaCleaner.Compatibility;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.LeavingSoon;

internal sealed class JellyfinLeavingSoonCollectionPublisher(
    ICollectionManager collectionManager,
    ILibraryManager libraryManager,
    ILogger<JellyfinLeavingSoonCollectionPublisher> logger) : ILeavingSoonCollectionPublisher
{
    private const string MarkerKey = "MediaCleanerLeavingSoon";
    private const string MarkerValue = "v1";

    public async Task<Guid> SynchronizeAsync(
        Guid? collectionId,
        string collectionName,
        IReadOnlyCollection<string> itemIds,
        CleanupCatalog catalog,
        CancellationToken cancellationToken)
    {
        var collection = FindOwnedCollection(collectionId);
        if (collection is null)
        {
            collection = await collectionManager.CreateCollectionAsync(new CollectionCreationOptions
            {
                Name = collectionName,
                IsLocked = false,
            }).ConfigureAwait(false);
            collection.ProviderIds[MarkerKey] = MarkerValue;
            await JellyfinCompatibility.UpdateMetadataAsync(libraryManager, collection, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Created owned Leaving Soon collection {CollectionId}", collection.Id);
        }
        else if (!string.Equals(collection.Name, collectionName, StringComparison.Ordinal))
        {
            collection.Name = collectionName;
            await JellyfinCompatibility.UpdateMetadataAsync(libraryManager, collection, cancellationToken).ConfigureAwait(false);
        }

        var desired = itemIds
            .Select(id => Guid.TryParse(id, out var parsed) && catalog.ItemsById.ContainsKey(id) ? parsed : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        var current = GetMemberIds(collection);

        // Add first: a transient failure must retain previously published warnings.
        var additions = desired.Except(current).ToList();
        if (additions.Count > 0)
        {
            await collectionManager.AddToCollectionAsync(collection.Id, additions).ConfigureAwait(false);
        }

        var removals = current.Except(desired).ToList();
        if (removals.Count > 0)
        {
            await collectionManager.RemoveFromCollectionAsync(collection.Id, removals).ConfigureAwait(false);
        }

        var verifiedCollection = libraryManager.GetItemById(collection.Id) as BoxSet
            ?? throw new InvalidOperationException("Owned Leaving Soon collection disappeared during synchronization.");
        var verified = GetMemberIds(verifiedCollection);
        if (!verified.SetEquals(desired))
        {
            throw new InvalidOperationException("Leaving Soon collection membership verification failed.");
        }

        return collection.Id;
    }

    // Direct members only. A recursive query also returns the episodes of linked
    // seasons on Jellyfin 12, which breaks the diff and the verification below.
    private static HashSet<Guid> GetMemberIds(BoxSet collection) =>
        collection.GetLinkedChildren().Select(x => x.Id).ToHashSet();

    private BoxSet? FindOwnedCollection(Guid? collectionId)
    {
        if (collectionId is { } id
            && libraryManager.GetItemById(id) is BoxSet stored
            && IsOwned(stored))
        {
            return stored;
        }

        return JellyfinCompatibility.GetItemList(libraryManager, new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.BoxSet],
                CollapseBoxSetItems = false,
                Recursive = true,
            })
            .OfType<BoxSet>()
            .FirstOrDefault(IsOwned);
    }

    private static bool IsOwned(BoxSet collection) =>
        collection.ProviderIds.TryGetValue(MarkerKey, out var value)
        && string.Equals(value, MarkerValue, StringComparison.Ordinal);
}
