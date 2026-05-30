using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace MediaCleaner;

internal class LeavingSoonCollectionService(ILogger<LeavingSoonCollectionService> logger, ILibraryManager libraryManager, ICollectionManager collectionManager)
{
    internal const string CollectionName = "Leaving Soon";
    private const string Tag = "Media Cleaner";

    private readonly List<Guid> _items = [];

    public async Task Finish()
    {
        try
        {
            logger.LogInformation(
                "Finishing collection {CollectionName} with {ItemCount} candidate item(s).",
                CollectionName,
                _items.Count);

            var collection = await GetBoxSetByName(CollectionName, _items.Count > 0);

            if (collection is not null)
            {
                var query = new InternalItemsQuery { CollapseBoxSetItems = false, Recursive = true, Parent = collection };
                var items = collection.GetItems(query).Items.Select(b => b.Id).ToList();

                logger.LogInformation(
                    "Removing {ItemCount} existing item(s) from collection {CollectionName}.",
                    items.Count,
                    CollectionName);
                await collectionManager.RemoveFromCollectionAsync(collection.Id, items);
            }
            else
            {
                logger.LogInformation(
                    "Collection {CollectionName} was not found and no candidate items require it to be created.",
                    CollectionName);
            }

            if (_items.Count > 0 && collection is not null)
            {
                logger.LogInformation(
                    "Adding {ItemCount} item(s) to collection {CollectionName}.",
                    _items.Count,
                    CollectionName);
                await collectionManager.AddToCollectionAsync(collection.Id, _items);
                await SetPhotoForCollection(collection);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error finishing collection {CollectionName}", CollectionName);
        }
    }

    public void AddItemRange(IEnumerable<Guid> ids)
    {
        _items.AddRange(ids);
    }

    internal static BoxSet? GetExistingCollection(ILibraryManager libraryManager) =>
        FindBoxSetByName(libraryManager, CollectionName);

    private async Task<BoxSet?> GetBoxSetByName(string name, bool create)
    {
        var collection = FindBoxSetByName(libraryManager, name);

        if (collection is null && create)
        {
            logger.LogInformation("{Name} not found, creating.", name);
            collection = await collectionManager.CreateCollectionAsync(new CollectionCreationOptions { Name = name, IsLocked = false });
            collection.Tags = [Tag];
            await libraryManager.UpdateItemAsync(
                collection,
                collection.GetParent(),
                ItemUpdateType.MetadataEdit,
                CancellationToken.None);
            logger.LogInformation("{Name} collection created with id {Id}.", name, collection.Id);
        }
        else if (collection is not null)
        {
            logger.LogInformation("{Name} collection found with id {Id}.", name, collection.Id);
        }

        return collection;
    }

    private static BoxSet? FindBoxSetByName(ILibraryManager libraryManager, string name) =>
        libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.BoxSet],
            CollapseBoxSetItems = false,
            Recursive = true,
            Tags = [Tag],
            Name = name,
        }).OfType<BoxSet>().FirstOrDefault();

    private async Task SetPhotoForCollection(BoxSet collection)
    {
        try
        {
            var query = new InternalItemsQuery { Recursive = true };

            var mediaItemWithImage = collection.GetItems(query)
                .Items
                .Where(item => item is Movie or Series or Season or Video)
                .FirstOrDefault(item =>
                    item.ImageInfos != null &&
                    item.ImageInfos.Any(i => i.Type == ImageType.Primary));

            if (mediaItemWithImage != null)
            {
                var imageInfo = mediaItemWithImage.ImageInfos
                    .First(i => i.Type == ImageType.Primary);

                collection.SetImage(new ItemImageInfo { Path = imageInfo.Path, Type = ImageType.Primary }, 0);

                await libraryManager.UpdateItemAsync(
                    collection,
                    collection.GetParent(),
                    ItemUpdateType.ImageUpdate,
                    CancellationToken.None);

                logger.LogTrace("Successfully set image for collection {CollectionName} from {ItemName}", collection.Name, mediaItemWithImage.Name);
            }
            else
            {
                logger.LogTrace("No items with images found in collection {CollectionName}", collection.Name);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error setting image for collection {CollectionName}", collection.Name);
        }
    }
}
