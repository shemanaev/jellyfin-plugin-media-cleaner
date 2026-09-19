using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using FluentAssertions;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaCleaner.Adapters;
using MediaCleaner.Controllers;
using MediaCleaner.Core;
using MediaCleaner.LeavingSoon;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

#if JELLYFIN_USER_IN_DATA_ENTITIES
using JellyfinUser = Jellyfin.Data.Entities.User;
#else
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;
#endif

namespace MediaCleaner.Tests;

public sealed class LeavingSoonControllerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "media-cleaner-controller-tests", Guid.NewGuid().ToString("N"));
    static LeavingSoonControllerTests() => SQLitePCL.Batteries_V2.Init();

    [Fact]
    public void UserListAppliesVisibilityBeforePaging()
    {
        var fixture = CreateFixture(false); fixture.Store.SynchronizeCandidates([Candidate()], DateTime.UtcNow);
        fixture.UserController.Get(0, 50).Value!.Items.Should().BeEmpty();
        var collectionId = Guid.Parse("30000000000000000000000000000001");
        fixture.Store.SetCollectionIds(collectionId, null);
        fixture.UserController.GetCollectionCards(collectionId.ToString("N")).Value!.Items.Should().BeEmpty();
    }

    [Fact]
    public void CollectionCardsRequireOwnedCollectionAndReturnDeadlineState()
    {
        var fixture = CreateFixture(true);
        var collectionId = Guid.Parse("30000000000000000000000000000001");
        fixture.Store.SetCollectionIds(collectionId, null);
        fixture.Store.SynchronizeCandidates([Candidate()], DateTime.UtcNow);
        fixture.Store.MarkPublished([ItemId], DateTime.UtcNow);

        fixture.UserController.GetCollectionCards(Guid.NewGuid().ToString("N")).Value!.IsOwned.Should().BeFalse();
        var response = fixture.UserController.GetCollectionCards(collectionId.ToString("N")).Value!;
        response.IsOwned.Should().BeTrue();
        response.Items.Should().ContainSingle(x => x.ItemId == ItemId && x.Status == "NoticeActive" && x.DeleteAfterUtc.HasValue);
    }

    [Fact]
    public void ItemActionTracksCandidateAndOwnProtection()
    {
        var fixture = CreateFixture(true);
        var collectionId = Guid.Parse("30000000000000000000000000000001");
        fixture.Store.SetCollectionIds(collectionId, null);
        fixture.Store.SynchronizeCandidates([Candidate()], DateTime.UtcNow);
        fixture.Store.MarkPublished([ItemId], DateTime.UtcNow);

        var candidate = fixture.UserController.GetProtectionAction(ItemId).Value!;
        candidate.ShowAction.Should().BeTrue();
        candidate.CanProtect.Should().BeTrue();
        candidate.IsProtected.Should().BeFalse();
        candidate.ProtectionTargetCount.Should().Be(1);
        candidate.CollectionId.Should().Be(collectionId.ToString("N"));
        candidate.Status.Should().Be("NoticeActive");
        candidate.DeleteAfterUtc.Should().NotBeNull();

        fixture.UserController.Protect(ItemId).Value.Should().NotBeNull();
        var protectedItem = fixture.UserController.GetProtectionAction(ItemId).Value!;
        protectedItem.ShowAction.Should().BeTrue();
        protectedItem.CanProtect.Should().BeFalse();
        protectedItem.IsProtected.Should().BeTrue();
        protectedItem.CollectionId.Should().Be(collectionId.ToString("N"));
        protectedItem.Status.Should().Be("ProtectedUntilWatched");
        protectedItem.DeleteAfterUtc.Should().NotBeNull();

        var otherUser = CreateFixture(true, fixture.Store, fixture.Playback, OtherUserId);
        var protectedByOther = otherUser.UserController.GetProtectionAction(ItemId).Value!;
        protectedByOther.IsProtected.Should().BeFalse();
        protectedByOther.CanProtect.Should().BeTrue();
        protectedByOther.Status.Should().Be("ProtectedByOtherUser");
        protectedByOther.DeleteAfterUtc.Should().NotBeNull();
    }

    [Fact]
    public void PartialEpisodeProtectionLeavesRemainingSeriesTargetsProtectable()
    {
        var protection = new ViewProtection(
            Guid.NewGuid().ToString("N"), EpisodeId, ItemId, UserId, "episode", DateTime.UtcNow,
            [EpisodeId, ItemId]);

        var coverage = LeavingSoonController.GetProtectionCoverage(
            [EpisodeId, OtherEpisodeId, "10000000000000000000000000000005"],
            [protection],
            UserId);

        coverage.Total.Should().Be(3);
        coverage.Protected.Should().Be(1);
        coverage.Unprotected.Should().Be(2);
        LeavingSoonController.HasProtectionForItem([protection], UserId, ItemId).Should().BeTrue();
    }

    [Fact]
    public void OwnProtectionCanBeRemovedAfterItemStopsBeingCandidate()
    {
        var fixture = CreateFixture(true);
        fixture.Store.SynchronizeCandidates([Candidate()], DateTime.UtcNow);
        fixture.UserController.Protect(ItemId).Value.Should().NotBeNull();
        fixture.Store.InvalidateWarnings();

        var action = fixture.UserController.GetProtectionAction(ItemId).Value!;

        action.ShowAction.Should().BeTrue();
        action.CanRemove.Should().BeTrue();
        action.CanProtect.Should().BeFalse();
    }

    [Fact]
    public void ReplacedItemIsNotShownAsProtectedAndCanBeProtectedAgain()
    {
        var fixture = CreateFixture(true);
        fixture.Store.SynchronizeCandidates([Candidate()], DateTime.UtcNow);
        fixture.UserController.Protect(ItemId).Value.Should().NotBeNull();
        var previousHash = fixture.Coordinator.ReadState().Protections.Single().ItemIdentityHash;
        fixture.Item.DateCreated = fixture.Item.DateCreated.AddDays(1);

        var replaced = fixture.UserController.GetProtectionAction(ItemId).Value!;
        replaced.IsProtected.Should().BeFalse();
        replaced.CanProtect.Should().BeTrue();
        fixture.UserController.Protect(ItemId).Value.Should().NotBeNull();

        var protection = fixture.Coordinator.ReadState().Protections.Single();
        protection.ItemIdentityHash.Should().NotBe(previousHash);
        protection.ItemIdentityHash.Should().Be(LeavingSoonCoordinator.IdentityHash(fixture.Item));
        fixture.UserController.GetProtectionAction(ItemId).Value!.IsProtected.Should().BeTrue();
    }

    [Fact]
    public void ControllersRequireAuthenticatedUserAndElevatedAdmin()
    {
        typeof(LeavingSoonController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>().Should().ContainSingle(x => x.Policy == null);
        typeof(LeavingSoonAdminController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>().Should().ContainSingle(x => x.Policy == Policies.RequiresElevation);
    }

    [Fact]
    public void UserCanRemoveOnlyOwnProtectionEvenWhenItemDisappears()
    {
        var owner = CreateFixture(true); owner.Store.SynchronizeCandidates([Candidate()], DateTime.UtcNow);
        var created = owner.UserController.Protect(ItemId).Value!;
        var protectionId = created.Protections.Single().ProtectionId;
        var other = CreateFixture(true, owner.Store, owner.Playback, OtherUserId);
        other.UserController.RemoveOwn(protectionId).Should().BeOfType<NotFoundResult>();
        owner.Coordinator.ReadState().Protections.Should().ContainSingle();
        owner.UserController.RemoveOwn(protectionId).Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public void UserCanRemoveEpisodeProtectionCreatedFromParent()
    {
        var fixture = CreateFixture(true);
        fixture.Store.SynchronizeCandidates([Candidate()], DateTime.UtcNow);
        fixture.Store.AddProtections(
            [new ViewProtectionSeed(EpisodeId, ItemId, "episode")],
            UserId,
            DateTime.UtcNow);

        fixture.UserController.RemoveOwnForItem(EpisodeId).Should().BeOfType<NoContentResult>();
        fixture.Coordinator.ReadState().Protections.Should().BeEmpty();
    }

    [Fact]
    public void AlreadyPlayedItemCannotBeProtected()
    {
        var fixture = CreateFixture(true); fixture.Store.SynchronizeCandidates([Candidate()], DateTime.UtcNow);
        fixture.Playback.Status = ProtectionPlaybackStatus.Played;
        var action = fixture.UserController.GetProtectionAction(ItemId).Value!;
        action.ShowAction.Should().BeTrue();
        action.CanProtect.Should().BeFalse();
        action.ProtectionTargetCount.Should().Be(0);
        fixture.UserController.Protect(ItemId).Result.Should().BeOfType<ConflictObjectResult>();
        fixture.Coordinator.ReadState().Protections.Should().BeEmpty();
    }

    [Fact]
    public void AdminListResolvesUserAndCanRemoveAllForItem()
    {
        var fixture = CreateFixture(true); fixture.Store.SynchronizeCandidates([Candidate()], DateTime.UtcNow);
        fixture.UserController.Protect(ItemId);
        var page = fixture.AdminController.GetProtections(0, 50, null).Value!;
        page.Items.Single().UserName.Should().Be("User");
        fixture.AdminController.RemoveAll(ItemId).Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public void AdminListReturnsFullRemoveAllCountAcrossPages()
    {
        var fixture = CreateFixture(true);
        fixture.Store.SynchronizeCandidates([Candidate()], DateTime.UtcNow);
        fixture.Store.AddProtections(
            [
                new ViewProtectionSeed(EpisodeId, ItemId, "episode-one"),
                new ViewProtectionSeed(OtherEpisodeId, ItemId, "episode-two"),
            ],
            UserId,
            DateTime.UtcNow);

        var page = fixture.AdminController.GetProtections(0, 1, null).Value!;

        page.Items.Should().ContainSingle();
        page.Items.Single().NoticeProtectionCount.Should().Be(2);
    }

    [Fact]
    public void AdminRemoveAllCountUsesSameAffectedItemsAsDelete()
    {
        var fixture = CreateFixture(true);
        fixture.Store.SynchronizeCandidates([Candidate()], DateTime.UtcNow);
        fixture.Store.AddProtections(
            [
                new ViewProtectionSeed(ItemId, ItemId, "movie", [ItemId]),
                new ViewProtectionSeed(EpisodeId, ItemId, "episode-one", [EpisodeId, ItemId]),
                new ViewProtectionSeed(OtherEpisodeId, OtherEpisodeId, "episode-two", [OtherEpisodeId, ItemId]),
            ],
            UserId,
            DateTime.UtcNow);

        var page = fixture.AdminController.GetProtections(0, 50, null).Value!;

        page.Items.Where(x => x.NoticeItemId == ItemId).Should().OnlyContain(x => x.NoticeProtectionCount == 3);
        fixture.AdminController.RemoveAll(ItemId).Should().BeOfType<NoContentResult>();
        fixture.Coordinator.ReadState().Protections.Should().BeEmpty();
    }

    [Fact]
    public async System.Threading.Tasks.Task AdminCanRunLeavingSoonRefresh()
    {
        var fixture = CreateFixture(true);

        var result = await fixture.AdminController.Refresh(System.Threading.CancellationToken.None);

        fixture.Refresher.CallCount.Should().Be(1);
        result.Value.Should().NotBeNull();
    }

    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private Fixture CreateFixture(bool visible, SqliteLeavingSoonStateStore? store = null, FakePlaybackReader? playback = null, string userId = UserId)
    {
        Directory.CreateDirectory(_directory);
        store ??= new SqliteLeavingSoonStateStore(Path.Combine(_directory, "state.db"), NullLogger<SqliteLeavingSoonStateStore>.Instance);
        playback ??= new FakePlaybackReader(); var clock = new FixedClock(DateTime.UtcNow);
        var coordinator = new LeavingSoonCoordinator(store, new NullPublisher(), playback,
            new CleanupPlanner(clock, new OrdinalPathMatcher(), new NoExtraFileProbe()), clock, NullLogger<LeavingSoonCoordinator>.Instance);
        var user = new JellyfinUser("User", "test", "test") { Id = Guid.Parse(userId), Username = userId == UserId ? "User" : "Other" };
        var item = new VisibilityMovie(visible) { Id = Guid.Parse(ItemId), Name = "Movie", Path = "/media/movie.mkv" };
        var library = new Mock<ILibraryManager>();
        library.Setup(x => x.GetItemById(Guid.Parse(ItemId))).Returns(item);
        var users = new Mock<IUserManager>(); users.Setup(x => x.GetUserById(Guid.Parse(userId))).Returns(user);
        users.Setup(x => x.GetUserById(Guid.Parse(UserId))).Returns(new JellyfinUser("User", "test", "test") { Id = Guid.Parse(UserId), Username = "User" });
        var userController = new LeavingSoonController(coordinator, library.Object, users.Object) { ControllerContext = Context(userId) };
        var refresher = new RecordingRefresher();
        var adminController = new LeavingSoonAdminController(coordinator, library.Object, users.Object, refresher) { ControllerContext = Context(userId) };
        return new Fixture(store, playback, coordinator, userController, adminController, refresher, item);
    }

    private static ControllerContext Context(string userId) => new() { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", userId)], "test")) } };
    private static LeavingSoonCandidate Candidate() => new(ItemId, MediaItemKind.Movie, "Movie", "/media/movie.mkv", "identity", "config", ["delete-rule"], 7);
    private const string ItemId = "10000000000000000000000000000001";
    private const string EpisodeId = "10000000000000000000000000000003";
    private const string OtherEpisodeId = "10000000000000000000000000000004";
    private const string UserId = "20000000000000000000000000000001";
    private const string OtherUserId = "20000000000000000000000000000002";

    private sealed record Fixture(SqliteLeavingSoonStateStore Store, FakePlaybackReader Playback, LeavingSoonCoordinator Coordinator, LeavingSoonController UserController, LeavingSoonAdminController AdminController, RecordingRefresher Refresher, VisibilityMovie Item);
    private sealed class VisibilityMovie(bool visible) : Movie { public override bool IsVisible(JellyfinUser user, bool ignoreHidden) => visible; }
    private sealed class FixedClock(DateTime value) : IClock { public DateTime UtcNow { get; } = value; }
    private sealed class FakePlaybackReader : ILeavingSoonPlaybackReader { public ProtectionPlaybackStatus Status { get; set; } = ProtectionPlaybackStatus.NotPlayed; public ProtectionPlaybackObservation Read(string itemId, string userId) => new(Status); }
    private sealed class RecordingRefresher : ILeavingSoonRefresher
    {
        public int CallCount { get; private set; }
        public System.Threading.Tasks.Task RefreshAsync(IProgress<double>? progress, System.Threading.CancellationToken cancellationToken)
        {
            CallCount++;
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }
    private sealed class NullPublisher : ILeavingSoonCollectionPublisher
    {
        public System.Threading.Tasks.Task<Guid> SynchronizeAsync(Guid? id, string name, IReadOnlyCollection<string> itemIds, CleanupCatalog catalog, System.Threading.CancellationToken token) => System.Threading.Tasks.Task.FromResult(id ?? Guid.NewGuid());
    }
}
