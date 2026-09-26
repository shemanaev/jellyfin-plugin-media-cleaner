using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MediaBrowser.Controller.Entities;
using MediaCleaner.Adapters;
using MediaCleaner.Core;
using MediaCleaner.LeavingSoon;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

#if JELLYFIN_USER_IN_DATA_ENTITIES
using JellyfinUser = Jellyfin.Data.Entities.User;
#else
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;
#endif

namespace MediaCleaner.Tests;

public sealed class LeavingSoonTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "media-cleaner-tests", Guid.NewGuid().ToString("N"));
    static LeavingSoonTests() => SQLitePCL.Batteries_V2.Init();

    [Fact]
    public async Task StartsNoticeOnlyAfterConfirmedPublication()
    {
        var fixture = CreateFixture();
        var created = fixture.Clock.UtcNow.AddDays(-27);
        var result = await fixture.Coordinator.PrepareAsync(CreatePolicy(30), CreateCatalog(created), Enabled(), CancellationToken.None);
        result.ProtectedItemIds.Should().Contain(ItemId);
        fixture.Publisher.ItemIds.Should().BeEquivalentTo(ItemId);
        fixture.Coordinator.ReadState().Warnings.Single().NotBeforeDeleteUtc.Should().Be(fixture.Clock.UtcNow.AddDays(7));
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(7);
        (await fixture.Coordinator.PrepareAsync(CreatePolicy(30), CreateCatalog(created), Enabled(), CancellationToken.None))
            .ProtectedItemIds.Should().NotContain(ItemId);
    }

    [Fact]
    public async Task FavoriteAndTagEligibilityComesFromRulesOnly()
    {
        var fixture = CreateFixture();
        var item = CreateItem(ItemId, MediaItemKind.Movie, fixture.Clock.UtcNow.AddDays(-27)) with
        {
            Tags = ["mediacleaner_keep"],
            Playback = [new PlaybackState(UserId, null, false, false, true)],
        };
        var catalog = new CleanupCatalog([], [item], new Dictionary<string, BaseItem>(), new Dictionary<string, JellyfinUser>());

        await fixture.Coordinator.PrepareAsync(CreatePolicy(30), catalog, Enabled(), CancellationToken.None);

        fixture.Publisher.ItemIds.Should().Contain(ItemId,
            "Leaving Soon must not add favorite or tag exclusions outside the configured rules");
    }

    [Fact]
    public async Task CompleteSeasonWithProtectedEpisodeIsNotPublished()
    {
        var fixture = CreateFixture();
        var created = fixture.Clock.UtcNow.AddDays(-27);
        var episode = CreateItem(EpisodeId, MediaItemKind.Episode, created) with { SeasonId = SeasonId, SeriesId = SeriesId, Tags = ["mediacleaner_keep"] };
        var season = CreateItem(SeasonId, MediaItemKind.Season, created) with { SeriesId = SeriesId, EpisodeIds = [EpisodeId] };
        var series = CreateItem(SeriesId, MediaItemKind.Series, created) with { SeasonIds = [SeasonId], EpisodeIds = [EpisodeId] };
        var movie = CreateItem(ItemId, MediaItemKind.Movie, created);
        var catalog = new CleanupCatalog([], [episode, season, series, movie], new Dictionary<string, BaseItem>(), new Dictionary<string, JellyfinUser>());
        var delete = CreatePolicy(30).Rules.Single();
        var completeSeasons = delete with
        {
            Id = "season-rule",
            Filters = delete.Filters with { MediaKinds = [MediaItemKind.Episode], DeleteEpisodes = SeriesDeleteKind.Season },
        };
        var keep = delete with
        {
            Id = "keep-rule",
            Trigger = new CleanupRuleTrigger(CleanupRuleTriggerKind.AddedAge, 0),
            Filters = delete.Filters with
            {
                MediaKinds = [MediaItemKind.Movie, MediaItemKind.Episode],
                EnableTagFilter = true,
                TagFilterMode = TagMode.Inclusion,
                Tags = ["mediacleaner_keep"],
            },
            Actions = new CleanupRuleActions(MediaCleaner.Core.CleanupRuleActionKind.Protect, false),
        };

        await fixture.Coordinator.PrepareAsync(new CleanupPolicy([delete with { Filters = delete.Filters with { MediaKinds = [MediaItemKind.Movie] } }, completeSeasons, keep], false), catalog, Enabled(), CancellationToken.None);

        fixture.Publisher.ItemIds.Should().Contain(ItemId);
        fixture.Publisher.ItemIds.Should().NotContain(SeasonId,
            "the deletion cascade blocks a season that contains a protected episode, so it must not be announced");
    }

    [Fact]
    public async Task EffectiveNoticeUsesMaximumAcrossMatchingDeleteRulesIncludingZeroOverride()
    {
        var fixture = CreateFixture();
        var first = CreatePolicy(30).Rules.Single() with { Id = "zero", Actions = new CleanupRuleActions(MediaCleaner.Core.CleanupRuleActionKind.Delete, false, 0) };
        var second = first with { Id = "positive", Name = "Positive", Actions = new CleanupRuleActions(MediaCleaner.Core.CleanupRuleActionKind.Delete, false, 5) };
        var policy = new CleanupPolicy([first, second], false);
        await fixture.Coordinator.PrepareAsync(policy, CreateCatalog(fixture.Clock.UtcNow.AddDays(-30)), Enabled(), CancellationToken.None);
        var warning = fixture.Coordinator.ReadState().Warnings.Single();
        warning.NoticeDays.Should().Be(5);
        warning.NotBeforeDeleteUtc.Should().Be(fixture.Clock.UtcNow.AddDays(5));
    }

    [Fact]
    public async Task ActivePublishedNoticeIsNotShortenedButCanBeExtended()
    {
        var fixture = CreateFixture(); var created = fixture.Clock.UtcNow.AddDays(-27); var catalog = CreateCatalog(created);
        await fixture.Coordinator.PrepareAsync(CreatePolicy(30), catalog, Enabled(), CancellationToken.None);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(1);
        await fixture.Coordinator.PrepareAsync(CreatePolicy(30), catalog, new MediaCleaner.Configuration.LeavingSoonConfiguration { Enabled = true, NoticeDays = 1 }, CancellationToken.None);
        fixture.Coordinator.ReadState().Warnings.Single().NotBeforeDeleteUtc.Should().Be(new DateTime(2026, 1, 8, 0, 0, 0, DateTimeKind.Utc));
        await fixture.Coordinator.PrepareAsync(CreatePolicy(30), catalog, new MediaCleaner.Configuration.LeavingSoonConfiguration { Enabled = true, NoticeDays = 14 }, CancellationToken.None);
        fixture.Coordinator.ReadState().Warnings.Single().NotBeforeDeleteUtc.Should().Be(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task ProtectionIsPerUserAndOnlyPlayedRemovesIt()
    {
        var fixture = CreateFixture();
        var catalog = CreateCatalog(fixture.Clock.UtcNow.AddDays(-27));
        await fixture.Coordinator.PrepareAsync(CreatePolicy(30), catalog, Enabled(), CancellationToken.None);
        var first = fixture.Coordinator.AddProtection(ItemId, UserId).Protection!;
        var second = fixture.Coordinator.AddProtection(ItemId, OtherUserId).Protection!;

        fixture.Playback.Set(UserId, ProtectionPlaybackStatus.NotPlayed);
        fixture.Playback.Set(OtherUserId, ProtectionPlaybackStatus.Played);
        var result = await fixture.Coordinator.PrepareAsync(CreatePolicy(30), catalog, Enabled(), CancellationToken.None);

        result.ProtectedItemIds.Should().Contain(ItemId);
        fixture.Coordinator.ReadState().Protections.Should().ContainSingle(x => x.ProtectionId == first.ProtectionId);
        fixture.Coordinator.ReadState().Protections.Should().NotContain(x => x.ProtectionId == second.ProtectionId);
    }

    [Fact]
    public async Task LastCompletedProtectionRequiresANewFullNotice()
    {
        var fixture = CreateFixture();
        var catalog = CreateCatalog(fixture.Clock.UtcNow.AddDays(-27));
        await fixture.Coordinator.PrepareAsync(CreatePolicy(30), catalog, Enabled(), CancellationToken.None);
        fixture.Coordinator.AddProtection(ItemId, UserId);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(20);
        fixture.Playback.Set(UserId, ProtectionPlaybackStatus.Played);

        var result = await fixture.Coordinator.PrepareAsync(CreatePolicy(30), catalog, Enabled(), CancellationToken.None);

        result.ProtectedItemIds.Should().Contain(ItemId);
        var warning = fixture.Coordinator.ReadState().Warnings.Single();
        warning.FirstPublishedUtc.Should().Be(fixture.Clock.UtcNow);
        warning.NotBeforeDeleteUtc.Should().Be(fixture.Clock.UtcNow.AddDays(7));
        fixture.Coordinator.ReadState().Protections.Should().BeEmpty();
    }

    [Fact]
    public async Task EpisodeGroupIsFixedAndResetsParentNoticeOnlyAfterLastEpisodeCompletes()
    {
        var fixture = CreateFixture();
        var catalog = CreateCatalog(fixture.Clock.UtcNow.AddDays(-27));
        await fixture.Coordinator.PrepareAsync(CreatePolicy(30), catalog, Enabled(), CancellationToken.None);
        var created = fixture.Coordinator.AddProtections(ItemId, UserId,
        [
            new ViewProtectionSeed(EpisodeId, ItemId, "episode-one"),
            new ViewProtectionSeed(OtherEpisodeId, ItemId, "episode-two"),
        ]);
        created.Protections.Should().HaveCount(2);
        created.Protections.Should().NotContain(x => x.ItemId == FutureEpisodeId);
        var originalPublished = fixture.Coordinator.ReadState().Warnings.Single().FirstPublishedUtc;
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(20);

        fixture.Playback.SetItem(EpisodeId, UserId, ProtectionPlaybackStatus.Played);
        var afterFirstEpisode = await fixture.Coordinator.PrepareAsync(CreatePolicy(30), catalog, Enabled(), CancellationToken.None);
        afterFirstEpisode.ProtectedItemIds.Should().Contain(ItemId);
        afterFirstEpisode.ProtectionReasons[ItemId].Should().Contain(x => x.Contains(OtherEpisodeId, StringComparison.OrdinalIgnoreCase));
        fixture.Coordinator.ReadState().Protections.Should().ContainSingle(x => x.ItemId == OtherEpisodeId);
        fixture.Coordinator.ReadState().Warnings.Single().FirstPublishedUtc.Should().Be(originalPublished);

        fixture.Playback.SetItem(OtherEpisodeId, UserId, ProtectionPlaybackStatus.Played);
        await fixture.Coordinator.PrepareAsync(CreatePolicy(30), catalog, Enabled(), CancellationToken.None);
        fixture.Coordinator.ReadState().Protections.Should().BeEmpty();
        fixture.Coordinator.ReadState().Warnings.Single().FirstPublishedUtc.Should().Be(fixture.Clock.UtcNow);
        fixture.Coordinator.ReadState().Warnings.Single().NotBeforeDeleteUtc.Should().Be(fixture.Clock.UtcNow.AddDays(7));
    }

    [Fact]
    public async Task DryRunProjectsFreshNoticeAfterPlayedProtectionWithoutMutatingState()
    {
        var fixture = CreateFixture();
        var catalog = CreateCatalog(fixture.Clock.UtcNow.AddDays(-27));
        await fixture.Coordinator.PrepareAsync(CreatePolicy(30), catalog, Enabled(), CancellationToken.None);
        fixture.Coordinator.AddProtection(ItemId, UserId);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(20);
        fixture.Playback.Set(UserId, ProtectionPlaybackStatus.Played);
        var before = fixture.Coordinator.ReadState();

        var result = await fixture.Coordinator.EvaluateReadOnlyAsync(CreatePolicy(30), catalog, Enabled(), CancellationToken.None);

        result.ProtectedItemIds.Should().Contain(ItemId);
        result.ProtectionReasons[ItemId].Should().Contain(x => x.Contains("2026-01-28", StringComparison.Ordinal));
        fixture.Coordinator.ReadState().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task DryRunProjectsNewCandidatesBeforeFirstRefresh()
    {
        var fixture = CreateFixture();
        var catalog = CreateCatalog(fixture.Clock.UtcNow.AddDays(-30));

        var result = await fixture.Coordinator.EvaluateReadOnlyAsync(CreatePolicy(30), catalog, Enabled(), CancellationToken.None);

        result.ProtectedItemIds.Should().Contain(ItemId);
        result.ProtectionReasons[ItemId].Should().Contain(x => x.Contains("2026-01-08", StringComparison.Ordinal));
        fixture.Coordinator.ReadState().Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task DisabledDryRunDoesNotRenewCompletedProtectionNotice()
    {
        var fixture = CreateFixture();
        var catalog = CreateCatalog(fixture.Clock.UtcNow.AddDays(-30));
        await fixture.Coordinator.PrepareAsync(CreatePolicy(30), catalog, Enabled(), CancellationToken.None);
        fixture.Coordinator.AddProtection(ItemId, UserId);
        fixture.Playback.Set(UserId, ProtectionPlaybackStatus.Played);
        var before = fixture.Coordinator.ReadState();

        var result = await fixture.Coordinator.EvaluateReadOnlyAsync(
            CreatePolicy(30),
            catalog,
            new MediaCleaner.Configuration.LeavingSoonConfiguration { Enabled = false },
            CancellationToken.None);

        result.ProtectedItemIds.Should().NotContain(ItemId);
        fixture.Coordinator.ReadState().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task PersonalProtectionRemainsWhenFeatureIsDisabled()
    {
        var fixture = CreateFixture();
        var catalog = CreateCatalog(fixture.Clock.UtcNow.AddDays(-27));
        await fixture.Coordinator.PrepareAsync(CreatePolicy(30), catalog, Enabled(), CancellationToken.None);
        fixture.Coordinator.AddProtection(ItemId, UserId);
        var result = await fixture.Coordinator.PrepareAsync(CreatePolicy(0), catalog,
            new MediaCleaner.Configuration.LeavingSoonConfiguration { Enabled = false }, CancellationToken.None);
        result.ProtectedItemIds.Should().Contain(ItemId);
        fixture.Coordinator.ReadState().Warnings.Should().BeEmpty();
    }

    [Fact]
    public void RemovingLastProtectionResetsPublishedWarning()
    {
        var fixture = CreateFixture();
        fixture.Store.SynchronizeCandidates([Candidate()], fixture.Clock.UtcNow);
        fixture.Store.MarkPublished([ItemId], fixture.Clock.UtcNow);
        var protection = fixture.Store.AddProtection(ItemId, UserId, "identity", fixture.Clock.UtcNow).Protection!;

        fixture.Store.RemoveProtection(protection.ProtectionId, UserId, fixture.Clock.UtcNow.AddDays(1));

        var warning = fixture.Store.Read().Warnings.Single();
        warning.Status.Should().Be(WarningStatus.PendingPublication);
        warning.FirstPublishedUtc.Should().BeNull();
        warning.NotBeforeDeleteUtc.Should().BeNull();
    }

    [Fact]
    public void RemovingProtectionByEpisodeIdFindsProtectionCreatedForParent()
    {
        var fixture = CreateFixture();
        fixture.Store.SynchronizeCandidates(
            [new LeavingSoonCandidate(SeriesId, MediaItemKind.Series, "Series", null, "series", "config", ["delete-rule"], 7)],
            fixture.Clock.UtcNow);
        fixture.Store.AddProtections(
            [new ViewProtectionSeed(EpisodeId, SeriesId, "episode")],
            UserId,
            fixture.Clock.UtcNow);

        var result = fixture.Store.RemoveProtectionsForOwnerItem(EpisodeId, UserId, fixture.Clock.UtcNow.AddDays(1));

        result.Changed.Should().BeTrue();
        result.Snapshot.Protections.Should().BeEmpty();
    }

    [Fact]
    public void SavingParentRebindsExistingEpisodeProtectionAndParentCanRemoveIt()
    {
        var fixture = CreateFixture();
        fixture.Store.SynchronizeCandidates(
            [new LeavingSoonCandidate(SeriesId, MediaItemKind.Series, "Series", null, "series", "config", ["delete-rule"], 7)],
            fixture.Clock.UtcNow);
        fixture.Store.AddProtections(
            [new ViewProtectionSeed(EpisodeId, EpisodeId, "episode", [EpisodeId])],
            UserId,
            fixture.Clock.UtcNow);

        var rebound = fixture.Store.AddProtections(
            [new ViewProtectionSeed(EpisodeId, SeriesId, "episode", [EpisodeId, SeasonId, SeriesId])],
            UserId,
            fixture.Clock.UtcNow.AddMinutes(1));

        rebound.Changed.Should().BeTrue();
        rebound.Snapshot.Protections.Should().ContainSingle().Which.NoticeItemId.Should().Be(SeriesId);
        rebound.Snapshot.Protections.Single().AffectedItemIds.Should().Contain([EpisodeId, SeasonId, SeriesId]);
        fixture.Store.RemoveProtectionsForOwnerItem(SeriesId, UserId, fixture.Clock.UtcNow.AddMinutes(2)).Changed.Should().BeTrue();
        fixture.Store.Read().Protections.Should().BeEmpty();
    }

    [Fact]
    public async Task CompletedEpisodeProtectionRestartsEpisodeSeasonAndSeriesNotices()
    {
        var fixture = CreateFixture();
        var created = fixture.Clock.UtcNow.AddDays(-30);
        var episode = CreateItem(EpisodeId, MediaItemKind.Episode, created) with { SeasonId = SeasonId, SeriesId = SeriesId };
        var season = CreateItem(SeasonId, MediaItemKind.Season, created) with { EpisodeIds = [EpisodeId], SeriesId = SeriesId };
        var series = CreateItem(SeriesId, MediaItemKind.Series, created) with { EpisodeIds = [EpisodeId], SeasonIds = [SeasonId] };
        var catalog = new CleanupCatalog([], [episode, season, series], new Dictionary<string, BaseItem>(), new Dictionary<string, JellyfinUser>());
        await fixture.Coordinator.PrepareAsync(CreatePolicy(30), catalog, Enabled(), CancellationToken.None);
        fixture.Coordinator.AddProtections(SeriesId, UserId, [new ViewProtectionSeed(EpisodeId, SeriesId, "episode")]);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(20);
        fixture.Playback.SetItem(EpisodeId, UserId, ProtectionPlaybackStatus.Played);

        await fixture.Coordinator.PrepareAsync(CreatePolicy(30), catalog, Enabled(), CancellationToken.None);

        var warnings = fixture.Coordinator.ReadState().Warnings.ToDictionary(x => x.ItemId, StringComparer.OrdinalIgnoreCase);
        warnings.Keys.Should().Contain([EpisodeId, SeasonId, SeriesId]);
        warnings[EpisodeId].FirstPublishedUtc.Should().Be(fixture.Clock.UtcNow);
        warnings[SeasonId].FirstPublishedUtc.Should().Be(fixture.Clock.UtcNow);
        warnings[SeriesId].FirstPublishedUtc.Should().Be(fixture.Clock.UtcNow);
    }

    [Fact]
    public async Task CompletedEpisodeProtectionFindsSeasonFromSparseCatalogEpisodeIds()
    {
        var fixture = CreateFixture();
        var created = fixture.Clock.UtcNow.AddDays(-30);
        var season = CreateItem(SeasonId, MediaItemKind.Season, created) with { EpisodeIds = [EpisodeId], SeriesId = SeriesId };
        var series = CreateItem(SeriesId, MediaItemKind.Series, created) with { EpisodeIds = [EpisodeId], SeasonIds = [SeasonId] };
        var catalog = new CleanupCatalog([], [season, series], new Dictionary<string, BaseItem>(), new Dictionary<string, JellyfinUser>());
        await fixture.Coordinator.PrepareAsync(CreatePolicy(30), catalog, Enabled(), CancellationToken.None);
        fixture.Coordinator.AddProtections(SeriesId, UserId, [new ViewProtectionSeed(EpisodeId, SeriesId, "episode")]);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(20);
        fixture.Playback.SetItem(EpisodeId, UserId, ProtectionPlaybackStatus.Played);

        await fixture.Coordinator.PrepareAsync(CreatePolicy(30), catalog, Enabled(), CancellationToken.None);

        var warnings = fixture.Coordinator.ReadState().Warnings.ToDictionary(x => x.ItemId, StringComparer.OrdinalIgnoreCase);
        warnings[SeasonId].FirstPublishedUtc.Should().Be(fixture.Clock.UtcNow);
        warnings[SeriesId].FirstPublishedUtc.Should().Be(fixture.Clock.UtcNow);
    }

    [Fact]
    public void ManualRemovalRestartsEveryStoredDeletionUnitNotice()
    {
        var fixture = CreateFixture();
        var candidates = new[]
        {
            new LeavingSoonCandidate(EpisodeId, MediaItemKind.Episode, "Episode", null, "episode", "config", ["delete-rule"], 7),
            new LeavingSoonCandidate(SeasonId, MediaItemKind.Season, "Season", null, "season", "config", ["delete-rule"], 7),
            new LeavingSoonCandidate(SeriesId, MediaItemKind.Series, "Series", null, "series", "config", ["delete-rule"], 7),
        };
        fixture.Store.SynchronizeCandidates(candidates, fixture.Clock.UtcNow);
        fixture.Store.MarkPublished(candidates.Select(x => x.ItemId).ToList(), fixture.Clock.UtcNow);
        var protection = fixture.Store.AddProtections(
            [new ViewProtectionSeed(EpisodeId, SeriesId, "episode", [EpisodeId, SeasonId, SeriesId])],
            UserId,
            fixture.Clock.UtcNow).Protection!;

        fixture.Store.RemoveProtection(protection.ProtectionId, UserId, fixture.Clock.UtcNow.AddDays(1));

        fixture.Store.Read().Warnings.Should().OnlyContain(x => x.FirstPublishedUtc == null && x.NotBeforeDeleteUtc == null);
    }

    [Fact]
    public async Task SparseCatalogHierarchyIsPersistedForLaterManualRemoval()
    {
        var fixture = CreateFixture();
        var created = fixture.Clock.UtcNow.AddDays(-30);
        var season = CreateItem(SeasonId, MediaItemKind.Season, created) with { EpisodeIds = [EpisodeId], SeriesId = SeriesId };
        var series = CreateItem(SeriesId, MediaItemKind.Series, created) with { EpisodeIds = [EpisodeId], SeasonIds = [SeasonId] };
        var catalog = new CleanupCatalog([], [season, series], new Dictionary<string, BaseItem>(), new Dictionary<string, JellyfinUser>());
        await fixture.Coordinator.PrepareAsync(CreatePolicy(30), catalog, Enabled(), CancellationToken.None);
        var protection = fixture.Coordinator.AddProtections(SeriesId, UserId,
            [new ViewProtectionSeed(EpisodeId, SeriesId, "episode")]).Protections.Single();
        await fixture.Coordinator.PrepareAsync(CreatePolicy(30), catalog, Enabled(), CancellationToken.None);

        fixture.Coordinator.RemoveOwnProtection(protection.ProtectionId, UserId);

        var warnings = fixture.Coordinator.ReadState().Warnings.ToDictionary(x => x.ItemId, StringComparer.OrdinalIgnoreCase);
        warnings[SeasonId].FirstPublishedUtc.Should().BeNull();
        warnings[SeriesId].FirstPublishedUtc.Should().BeNull();
    }

    [Fact]
    public async Task NewNoticeGenerationCannotReusePublishedDeadline()
    {
        var fixture = CreateFixture();
        var catalog = CreateCatalog(fixture.Clock.UtcNow.AddDays(-30));
        var first = Enabled();
        first.NoticeGeneration = "before-disable";
        await fixture.Coordinator.PrepareAsync(CreatePolicy(30), catalog, first, CancellationToken.None);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(20);

        var reenabled = Enabled();
        reenabled.NoticeGeneration = "after-reenable";
        await fixture.Coordinator.PrepareAsync(CreatePolicy(30), catalog, reenabled, CancellationToken.None);

        var warning = fixture.Coordinator.ReadState().Warnings.Single();
        warning.NoticeGeneration.Should().Be("after-reenable");
        warning.FirstPublishedUtc.Should().Be(fixture.Clock.UtcNow);
        warning.NotBeforeDeleteUtc.Should().Be(fixture.Clock.UtcNow.AddDays(7));
    }

    [Fact]
    public async Task DisableInvalidatesWarningsEvenWhenCollectionClearFails()
    {
        var fixture = CreateFixture();
        var catalog = CreateCatalog(fixture.Clock.UtcNow.AddDays(-30));
        await fixture.Coordinator.PrepareAsync(CreatePolicy(30), catalog, Enabled(), CancellationToken.None);
        fixture.Publisher.FailNext = true;

        var action = () => fixture.Coordinator.RefreshDisabledAsync(
            catalog,
            new MediaCleaner.Configuration.LeavingSoonConfiguration { Enabled = false },
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>();
        fixture.Coordinator.ReadState().Warnings.Should().BeEmpty();

        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(20);
        await fixture.Coordinator.PrepareAsync(CreatePolicy(30), catalog, Enabled(), CancellationToken.None);
        var warning = fixture.Coordinator.ReadState().Warnings.Single();
        warning.FirstPublishedUtc.Should().Be(fixture.Clock.UtcNow);
        warning.NotBeforeDeleteUtc.Should().Be(fixture.Clock.UtcNow.AddDays(7));
    }

    [Fact]
    public void RevisionCanBeReadWithoutLoadingSnapshot()
    {
        var fixture = CreateFixture();
        fixture.Store.SynchronizeCandidates([Candidate()], fixture.Clock.UtcNow);

        fixture.Store.ReadRevision().Should().Be(fixture.Store.Read().Revision);
    }

    [Fact]
    public void VersionTwoRequestsMigratePendingAndAcceptedButNotRejected()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "state.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE metadata (singleton INTEGER PRIMARY KEY,schema_version INTEGER NOT NULL,revision INTEGER NOT NULL,collection_id TEXT NULL,ready_collection_id TEXT NULL);
                INSERT INTO metadata VALUES (1,2,7,NULL,NULL);
                CREATE TABLE warning_entries (item_id TEXT PRIMARY KEY,kind INTEGER NOT NULL,status INTEGER NOT NULL,item_name TEXT NOT NULL,item_path TEXT NULL,identity_hash TEXT NOT NULL,config_fingerprint TEXT NOT NULL,first_detected_utc TEXT NOT NULL,first_published_utc TEXT NULL,not_before_delete_utc TEXT NULL,last_seen_utc TEXT NOT NULL,notice_days INTEGER NOT NULL,last_error TEXT NULL);
                CREATE TABLE warning_entry_rules (item_id TEXT NOT NULL,rule_id TEXT NOT NULL,PRIMARY KEY(item_id,rule_id));
                INSERT INTO warning_entries VALUES ('{ItemId}',0,1,'Movie',NULL,'identity','config','2026-01-01T00:00:00Z','2026-01-01T00:00:00Z','2026-01-08T00:00:00Z','2026-01-01T00:00:00Z',7,NULL);
                CREATE TABLE keep_requests (request_id TEXT PRIMARY KEY,item_id TEXT NOT NULL,requested_by_user_id TEXT NOT NULL,source TEXT NOT NULL,requested_at_utc TEXT NOT NULL,comment TEXT NULL,status INTEGER NOT NULL,reviewed_by_user_id TEXT NULL,reviewed_at_utc TEXT NULL,review_comment TEXT NULL,UNIQUE(item_id,requested_by_user_id));
                CREATE TABLE keep_request_events (event_id INTEGER PRIMARY KEY AUTOINCREMENT,request_id TEXT,item_id TEXT,requested_by_user_id TEXT,kind INTEGER,actor_user_id TEXT,occurred_at_utc TEXT,comment TEXT);
                INSERT INTO keep_requests VALUES ('10000000000000000000000000000101','{ItemId}','{UserId}','legacy','2026-01-01T00:00:00Z',NULL,0,NULL,NULL,NULL);
                INSERT INTO keep_requests VALUES ('10000000000000000000000000000102','{ItemId}','{OtherUserId}','legacy','2026-01-01T00:00:00Z',NULL,1,NULL,NULL,NULL);
                INSERT INTO keep_requests VALUES ('10000000000000000000000000000103','{OtherItemId}','30000000000000000000000000000003','legacy','2026-01-01T00:00:00Z',NULL,2,NULL,NULL,NULL);
                """;
            command.ExecuteNonQuery();
        }
        var store = new SqliteLeavingSoonStateStore(path, NullLogger<SqliteLeavingSoonStateStore>.Instance);
        var state = store.Read();
        state.Revision.Should().Be(7);
        state.Protections.Select(x => x.UserId).Should().BeEquivalentTo(UserId, OtherUserId);
        state.Warnings.Single().FirstPublishedUtc.Should().BeNull();
        state.Warnings.Single().NoticeGeneration.Should().Be("initial");
        state.Protections.Should().OnlyContain(x => x.AffectedItemIds!.Contains(x.ItemId));
    }

    [Fact]
    public void ExternalProtectionBlocksParentCascade()
    {
        var fixture = CreateFixture();
        var episode = CreateItem(ItemId, MediaItemKind.Episode, fixture.Clock.UtcNow.AddDays(-10)) with { SeasonId = SeasonId, SeriesId = SeriesId };
        var season = CreateItem(SeasonId, MediaItemKind.Season, fixture.Clock.UtcNow.AddDays(-10)) with { EpisodeIds = [ItemId], SeriesId = SeriesId };
        var series = CreateItem(SeriesId, MediaItemKind.Series, fixture.Clock.UtcNow.AddDays(-10)) with { EpisodeIds = [ItemId], SeasonIds = [SeasonId] };
        var plan = new CleanupPlanner(fixture.Clock, new OrdinalPathMatcher(), new NoExtraFileProbe())
            .Plan(new CleanupRequest(CreatePolicy(0), [], [episode, season, series], false, [ItemId]));
        plan.Deletions.Select(x => x.ItemId).Should().NotContain([ItemId, SeasonId, SeriesId]);
    }

    [Fact]
    public void ProtectedEpisodeOutsideSparseCatalogStillBlocksParentCascade()
    {
        var fixture = CreateFixture();
        var season = CreateItem(SeasonId, MediaItemKind.Season, fixture.Clock.UtcNow.AddDays(-10)) with { EpisodeIds = [EpisodeId], SeriesId = SeriesId };
        var plan = new CleanupPlanner(fixture.Clock, new OrdinalPathMatcher(), new NoExtraFileProbe())
            .Plan(new CleanupRequest(CreatePolicy(0), [], [season], false, [EpisodeId]));
        plan.Deletions.Should().BeEmpty();
    }

    [Fact]
    public void DryRunUsesDefaultReasonWhenStructuredReasonsAreAbsent()
    {
        var fixture = CreateFixture();
        var item = CreateItem(ItemId, MediaItemKind.Movie, fixture.Clock.UtcNow.AddDays(-10));
        var plan = new CleanupPlanner(fixture.Clock, new OrdinalPathMatcher(), new NoExtraFileProbe())
            .Plan(new CleanupRequest(CreatePolicy(0), [], [item], true, [ItemId]));
        plan.AuditEntries.Should().ContainSingle(x => x.Stage == CleanupAuditStage.ExternalProtection
            && x.Reason == "protected by an external safety exclusion");
    }

    [Fact]
    public void DryRunEmitsEveryStructuredProtectionReasonAndFallsBackPerItem()
    {
        var fixture = CreateFixture();
        var first = CreateItem(ItemId, MediaItemKind.Movie, fixture.Clock.UtcNow.AddDays(-10));
        var second = CreateItem(OtherItemId, MediaItemKind.Movie, fixture.Clock.UtcNow.AddDays(-10));
        IReadOnlyDictionary<string, IReadOnlyList<string>> reasons = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [ItemId] = ["protected by Alice", "notice is active"],
        };
        var plan = new CleanupPlanner(fixture.Clock, new OrdinalPathMatcher(), new NoExtraFileProbe())
            .Plan(new CleanupRequest(CreatePolicy(0), [], [first, second], true, [ItemId, OtherItemId], reasons));
        plan.AuditEntries.Where(x => x.Stage == CleanupAuditStage.ExternalProtection && x.ItemId == ItemId)
            .Select(x => x.Reason).Should().Equal("protected by Alice", "notice is active");
        plan.AuditEntries.Should().Contain(x => x.Stage == CleanupAuditStage.Protection
            && x.Outcome == CleanupAuditOutcome.Suppressed
            && x.ItemId == ItemId
            && x.Reason == "delete suppressed because protected by Alice; notice is active");
        plan.AuditEntries.Should().Contain(x => x.Stage == CleanupAuditStage.ExternalProtection && x.ItemId == OtherItemId
            && x.Reason == "protected by an external safety exclusion");
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private Fixture CreateFixture()
    {
        var clock = new MutableClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var store = new SqliteLeavingSoonStateStore(Path.Combine(_directory, "state.db"), NullLogger<SqliteLeavingSoonStateStore>.Instance);
        var publisher = new RecordingPublisher(); var playback = new FakePlaybackReader();
        var coordinator = new LeavingSoonCoordinator(store, publisher, playback,
            new CleanupPlanner(clock, new OrdinalPathMatcher(), new NoExtraFileProbe()), clock, NullLogger<LeavingSoonCoordinator>.Instance);
        return new Fixture(clock, store, publisher, playback, coordinator);
    }

    private static MediaCleaner.Configuration.LeavingSoonConfiguration Enabled() => new() { Enabled = true, NoticeDays = 7 };
    private static CleanupCatalog CreateCatalog(DateTime created) => new([], [CreateItem(ItemId, MediaItemKind.Movie, created)], new Dictionary<string, BaseItem>(), new Dictionary<string, JellyfinUser>());
    private static MediaItem CreateItem(string id, MediaItemKind kind, DateTime created) => new(id, kind, "Test", "Test", created, null, null, [], []);
    private static LeavingSoonCandidate Candidate() => new(ItemId, MediaItemKind.Movie, "Movie", null, "identity", "config", ["delete-rule"], 7);
    private static CleanupPolicy CreatePolicy(int days) => new([new CleanupRule("delete-rule", "Delete", true,
        new CleanupRuleTrigger(CleanupRuleTriggerKind.AddedAge, days), new CleanupRuleFilters(
            [MediaItemKind.Movie, MediaItemKind.Episode, MediaItemKind.Season, MediaItemKind.Series], [], UsersListMode.Ignore, [], UsersListMode.Ignore,
            RuleFavoriteFilterKind.Ignore, [], LocationsListMode.Exclude, false, TagMode.Exclusion, [], SeriesDeleteKind.Episode, SeriesKeepKind.None),
        new CleanupRuleActions(MediaCleaner.Core.CleanupRuleActionKind.Delete, false))], false);

    private const string ItemId = "10000000000000000000000000000001";
    private const string OtherItemId = "10000000000000000000000000000004";
    private const string EpisodeId = "10000000000000000000000000000005";
    private const string OtherEpisodeId = "10000000000000000000000000000006";
    private const string FutureEpisodeId = "10000000000000000000000000000007";
    private const string SeasonId = "10000000000000000000000000000002";
    private const string SeriesId = "10000000000000000000000000000003";
    private const string UserId = "20000000000000000000000000000001";
    private const string OtherUserId = "20000000000000000000000000000002";

    private sealed record Fixture(MutableClock Clock, SqliteLeavingSoonStateStore Store, RecordingPublisher Publisher, FakePlaybackReader Playback, LeavingSoonCoordinator Coordinator);
    private sealed class MutableClock(DateTime value) : IClock { public DateTime UtcNow { get; set; } = value; }
    private sealed class FakePlaybackReader : ILeavingSoonPlaybackReader
    {
        private readonly Dictionary<string, ProtectionPlaybackStatus> users = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ProtectionPlaybackStatus> items = new(StringComparer.OrdinalIgnoreCase);
        public void Set(string userId, ProtectionPlaybackStatus status) => users[userId] = status;
        public void SetItem(string itemId, string userId, ProtectionPlaybackStatus status) => items[$"{itemId}|{userId}"] = status;
        public ProtectionPlaybackObservation Read(string itemId, string userId) =>
            new(items.GetValueOrDefault($"{itemId}|{userId}", users.GetValueOrDefault(userId, ProtectionPlaybackStatus.NotPlayed)));
    }
    private sealed class RecordingPublisher : ILeavingSoonCollectionPublisher
    {
        public IReadOnlyCollection<string> ItemIds { get; private set; } = [];
        public bool FailNext { get; set; }
        public Task<Guid> SynchronizeAsync(Guid? id, string name, IReadOnlyCollection<string> itemIds, CleanupCatalog catalog, CancellationToken token)
        {
            ItemIds = itemIds;
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("publication failed");
            }
            return Task.FromResult(id ?? Guid.Parse("30000000000000000000000000000001"));
        }
    }
}
