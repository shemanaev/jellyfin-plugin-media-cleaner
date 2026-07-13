using FluentAssertions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Activity;
using MediaCleaner.Adapters;
using MediaCleaner.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

#if JELLYFIN_10_10
using JellyfinActivityLog = Jellyfin.Data.Entities.ActivityLog;
using JellyfinUser = Jellyfin.Data.Entities.User;
#else
using JellyfinActivityLog = Jellyfin.Database.Implementations.Entities.ActivityLog;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;
#endif

namespace MediaCleaner.Tests;

public class JellyfinMutationAdapterTests
{
    [Fact]
    public async Task ExecuteAsync_NotifiesOnlySuccessfullyDeletedDecisionsAfterDeletion()
    {
        var blockedItem = Item("blocked-season", MediaItemKind.Season, "Protected season");
        var deletedItem = Item("deleted-movie", MediaItemKind.Movie, "Deleted movie");
        var blockedDecision = Decision(blockedItem, markUnplayedUserIds: ["user"]);
        var deletedDecision = Decision(deletedItem, markUnplayedUserIds: ["user"]);
        var events = new List<string>();
        var blockedEntity = new RecordingMovie { Id = Guid.NewGuid(), Name = blockedItem.Name };
        var deletedEntity = new RecordingMovie
        {
            Id = Guid.NewGuid(),
            Name = deletedItem.Name,
            MarkUnplayedCallback = () => events.Add("mark-unplayed"),
        };
        var user = new JellyfinUser("User", "test", "test");
        var activities = new List<JellyfinActivityLog>();
        var libraryManager = new Mock<ILibraryManager>();
        var activityManager = new Mock<IActivityManager>();
        libraryManager
            .Setup(x => x.DeleteItem(deletedEntity, It.IsAny<DeleteOptions>(), true))
            .Callback(() => events.Add("delete"));
        activityManager
            .Setup(x => x.CreateAsync(It.IsAny<JellyfinActivityLog>()))
            .Callback<JellyfinActivityLog>(activity =>
            {
                events.Add("activity");
                activities.Add(activity);
            })
            .Returns(Task.CompletedTask);
        var adapter = new JellyfinMutationAdapter(
            NullLogger<JellyfinMutationAdapter>.Instance,
            libraryManager.Object,
            activityManager.Object);
        var plan = new CleanupPlan(
            [blockedDecision, deletedDecision],
            [new DeletionOperation(deletedItem.Id, deletedItem.Kind, deletedItem.Name)],
            []);
        var catalog = Catalog(
            new Dictionary<string, JellyfinUser>(StringComparer.OrdinalIgnoreCase) { ["user"] = user },
            (blockedItem.Id, blockedEntity),
            (deletedItem.Id, deletedEntity));

        await adapter.ExecuteAsync(plan, catalog, CancellationToken.None);

        events.Should().Equal("delete", "activity", "mark-unplayed");
        activities.Should().ContainSingle();
        activities[0].Name.Should().Be(deletedDecision.Notification.Title);
        activities.Should().NotContain(activity => activity.Name == blockedDecision.Notification.Title);
        libraryManager.Verify(x => x.DeleteItem(blockedEntity, It.IsAny<DeleteOptions>(), true), Times.Never);
        blockedEntity.MarkUnplayedCount.Should().Be(0);
        deletedEntity.MarkUnplayedCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotNotifyWhenDeletionFails()
    {
        var item = Item("failed", MediaItemKind.Movie, "Failed movie");
        var entity = new RecordingMovie { Id = Guid.NewGuid(), Name = item.Name };
        var user = new JellyfinUser("User", "test", "test");
        var libraryManager = new Mock<ILibraryManager>();
        var activityManager = new Mock<IActivityManager>();
        libraryManager
            .Setup(x => x.DeleteItem(entity, It.IsAny<DeleteOptions>(), true))
            .Throws(new InvalidOperationException("delete failed"));
        var adapter = new JellyfinMutationAdapter(
            NullLogger<JellyfinMutationAdapter>.Instance,
            libraryManager.Object,
            activityManager.Object);
        var decision = Decision(item, markUnplayedUserIds: ["user"]);
        var plan = new CleanupPlan(
            [decision],
            [new DeletionOperation(item.Id, item.Kind, item.Name)],
            []);
        var catalog = Catalog(
            new Dictionary<string, JellyfinUser>(StringComparer.OrdinalIgnoreCase) { ["user"] = user },
            (item.Id, entity));

        await adapter.ExecuteAsync(plan, catalog, CancellationToken.None);

        activityManager.Verify(x => x.CreateAsync(It.IsAny<JellyfinActivityLog>()), Times.Never);
        entity.MarkUnplayedCount.Should().Be(0);
    }

    [Fact]
    public void GetSuccessfullyDeletedDecisions_ExcludesBlockedAndFailedItems()
    {
        var blocked = Decision(Item("blocked", MediaItemKind.Season, "Blocked"), markUnplayedUserIds: ["user"]);
        var failed = Decision(Item("failed", MediaItemKind.Movie, "Failed"), markUnplayedUserIds: ["user"]);
        var deleted = Decision(Item("deleted", MediaItemKind.Movie, "Deleted"), markUnplayedUserIds: ["user"]);

        var result = JellyfinMutationAdapter.GetSuccessfullyDeletedDecisions(
                [blocked, failed, deleted],
                new HashSet<string>(["DELETED"], StringComparer.OrdinalIgnoreCase))
            .ToList();

        result.Should().ContainSingle().Which.Should().BeSameAs(deleted);
    }

    private static CleanupCatalog Catalog(params (string Id, BaseItem Item)[] items) =>
        Catalog(new Dictionary<string, JellyfinUser>(StringComparer.OrdinalIgnoreCase), items);

    private static CleanupCatalog Catalog(
        IReadOnlyDictionary<string, JellyfinUser> users,
        params (string Id, BaseItem Item)[] items) =>
        new(
            [],
            [],
            items.ToDictionary(x => x.Id, x => x.Item, StringComparer.OrdinalIgnoreCase),
            users);

    private static CleanupDecision Decision(MediaItem item, IReadOnlyList<string>? markUnplayedUserIds = null) =>
        new(
            item,
            ExpiredKind.Played,
            [],
            "test reason",
            new ActivityNotification($"{item.Name} was deleted", "test", item.Path ?? string.Empty),
            markUnplayedUserIds ?? [],
            ["test rule"]);

    private static MediaItem Item(string id, MediaItemKind kind, string name) =>
        new(
            id,
            kind,
            name,
            name,
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            $"/media/{id}",
            "/media",
            [],
            []);

    private sealed class RecordingMovie : Movie
    {
        public int MarkUnplayedCount { get; private set; }

        public Action? MarkUnplayedCallback { get; init; }

        public override void MarkUnplayed(JellyfinUser user)
        {
            MarkUnplayedCount++;
            MarkUnplayedCallback?.Invoke();
        }
    }
}
