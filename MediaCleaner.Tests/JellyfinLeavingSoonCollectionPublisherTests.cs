using FluentAssertions;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaCleaner.Adapters;
using MediaCleaner.LeavingSoon;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

#if JELLYFIN_USER_IN_DATA_ENTITIES
using JellyfinUser = Jellyfin.Data.Entities.User;
#else
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;
#endif

namespace MediaCleaner.Tests;

[CollectionDefinition("Jellyfin collection publisher", DisableParallelization = true)]
public sealed class JellyfinCollectionPublisherCollection;

[Collection("Jellyfin collection publisher")]
public class JellyfinLeavingSoonCollectionPublisherTests
{
    [Fact]
    public async Task SynchronizeAsync_ExistingLinkedSeason_DoesNotTreatItsEpisodesAsCollectionMembers()
    {
        var collectionId = Guid.NewGuid();
        var season = new Season { Id = Guid.NewGuid() };
        var episode = new Episode { Id = Guid.NewGuid(), ParentId = season.Id };
        var collection = new BoxSet
        {
            Id = collectionId,
            Name = "Leaving Soon",
            LinkedChildren = [new LinkedChild { ItemId = season.Id }],
        };
        collection.ProviderIds["MediaCleanerLeavingSoon"] = "v1";

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(x => x.GetItemById(collectionId)).Returns(collection);
        libraryManager.Setup(x => x.GetItemById(season.Id)).Returns(season);
        libraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(query =>
                query.ItemIds?.Contains(season.Id) == true ? [season] : [season, episode]);
        var collectionManager = new Mock<ICollectionManager>();
        var publisher = new JellyfinLeavingSoonCollectionPublisher(
            collectionManager.Object,
            libraryManager.Object,
            NullLogger<JellyfinLeavingSoonCollectionPublisher>.Instance);
        var catalog = new CleanupCatalog([], [],
            new Dictionary<string, BaseItem> { [season.Id.ToString("N")] = season },
            new Dictionary<string, JellyfinUser>());

        var previousLibraryManager = BaseItem.LibraryManager;
        try
        {
            BaseItem.LibraryManager = libraryManager.Object;
            var result = await publisher.SynchronizeAsync(
                collectionId, "Leaving Soon", [season.Id.ToString("N")], catalog, CancellationToken.None);

            result.Should().Be(collectionId);
            collectionManager.Verify(x => x.AddToCollectionAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<Guid>>()), Times.Never);
            collectionManager.Verify(x => x.RemoveFromCollectionAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<Guid>>()), Times.Never);
        }
        finally
        {
            BaseItem.LibraryManager = previousLibraryManager;
        }
    }
}
