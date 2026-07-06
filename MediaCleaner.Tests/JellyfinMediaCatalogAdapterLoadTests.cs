using FluentAssertions;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaCleaner.Adapters;
using MediaCleaner.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

#if JELLYFIN_USER_IN_DATA_ENTITIES
using JellyfinUser = Jellyfin.Data.Entities.User;
#else
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;
#endif

namespace MediaCleaner.Tests;

public class JellyfinMediaCatalogAdapterLoadTests
{
    private const int ProgramCount = 72;
    private const int EpisodeCount = 3253;
    private static readonly DateTime Now = new(2026, 7, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact(Timeout = 15_000)]
    public async Task Create_LoadShapedEpisodeRules_DeduplicatesJellyfinQueriesAndSnapshotLookups()
    {
        var users = Enumerable.Range(0, 3).Select(CreateUser).ToList();
        var libraryManager = new Mock<ILibraryManager>();
        var library = TestLibrary.Create(libraryManager.Object);
        var userData = new CountingUserDataManager(users, library.AllItems);
        var hierarchy = new CountingTvHierarchyProvider(library);
        var itemQueries = new Dictionary<(BaseItemKind Kind, string UserId, ItemSortBy SortBy), int>();
        SetupUsers(libraryManager, users);
        SetupLibrary(libraryManager, library, itemQueries);

        var adapter = new JellyfinMediaCatalogAdapter(
            NullLogger<JellyfinMediaCatalogAdapter>.Instance,
            CreateUserManager(users),
            libraryManager.Object,
            userData.Manager,
            hierarchy);

        var catalog = await Task.Run(() => adapter.Create(new CleanupPolicy(
                [
                    EpisodeRule("played-short", CleanupRuleTriggerKind.Played, 10, SeriesKeepKind.Last),
                    EpisodeRule("played-wide", CleanupRuleTriggerKind.Played, 30, SeriesKeepKind.Last),
                    EpisodeRule("not-played", CleanupRuleTriggerKind.NotPlayed, 60, SeriesKeepKind.None),
                ],
                AllowDeleteIfPlayedBeforeAdded: false), CancellationToken.None));

        catalog.Items.Count(x => x.Kind == MediaItemKind.Episode).Should().Be(EpisodeCount);
        catalog.Items.Count(x => x.Kind == MediaItemKind.Season).Should().Be(ProgramCount);
        catalog.Items.Count(x => x.Kind == MediaItemKind.Series).Should().Be(ProgramCount);
        itemQueries.Values.Sum().Should().Be(users.Count * 2, "DatePlayed and DateCreated are each loaded once per user");
        hierarchy.SeasonEpisodeCalls.Values.Sum().Should().Be(ProgramCount);
        hierarchy.SeriesEpisodeCalls.Values.Sum().Should().Be(ProgramCount);
        hierarchy.SeriesSeasonCalls.Values.Sum().Should().Be(ProgramCount);
        userData.TotalCalls.Should().Be(users.Count * catalog.Items.Count, "candidate checks and playback snapshots should share user-data cache entries");
    }

    [Fact]
    public void Create_OverlappingPlayedRules_KeepUnionOfRuleCandidateWindows()
    {
        var user = CreateUser(0);
        var movie = new Movie
        {
            Id = GuidFrom(10),
            Name = "Movie",
            Path = "/media/movie.mkv",
            DateCreated = Now.AddDays(-100),
        };
        var libraryManager = new Mock<ILibraryManager>();
        var userData = new CountingUserDataManager([user], [movie]);
        userData.Set(user, movie, PlayedData(Now.AddDays(-20)));
        SetupUsers(libraryManager, [user]);
        SetupLibrary(
            libraryManager,
            [movie],
            new Dictionary<(BaseItemKind Kind, string UserId, ItemSortBy SortBy), int>());

        var adapter = new JellyfinMediaCatalogAdapter(
            NullLogger<JellyfinMediaCatalogAdapter>.Instance,
            CreateUserManager([user]),
            libraryManager.Object,
            userData.Manager,
            new CountingTvHierarchyProvider(TestLibrary.Empty));

        var catalog = adapter.Create(new CleanupPolicy(
            [
                Rule("short-window", MediaItemKind.Movie, CleanupRuleTriggerKind.Played, countAsNotPlayedAfter: 5),
                Rule("wide-window", MediaItemKind.Movie, CleanupRuleTriggerKind.Played, countAsNotPlayedAfter: 30),
            ],
            AllowDeleteIfPlayedBeforeAdded: false), CancellationToken.None);

        catalog.Items.Should().ContainSingle(x => x.Id == movie.Id.ToString("N"));
    }

    [Fact]
    public void Create_FavoriteFilter_HonorsInheritedSeriesFavorite()
    {
        var user = CreateUser(0);
        var libraryManager = new Mock<ILibraryManager>();
        var library = TestLibrary.Create(libraryManager.Object, programCount: 1, episodeCount: 1);
        var userData = new CountingUserDataManager([user], library.AllItems);
        var episode = library.Episodes.Single();
        var series = library.Series.Single();
        userData.Set(user, episode, PlayedData(Now.AddDays(-30)));
        userData.Set(user, series, new UserItemData { Key = series.Id.ToString("N"), IsFavorite = true });
        SetupUsers(libraryManager, [user]);
        SetupLibrary(
            libraryManager,
            library,
            new Dictionary<(BaseItemKind Kind, string UserId, ItemSortBy SortBy), int>());

        var adapter = new JellyfinMediaCatalogAdapter(
            NullLogger<JellyfinMediaCatalogAdapter>.Instance,
            CreateUserManager([user]),
            libraryManager.Object,
            userData.Manager,
            new CountingTvHierarchyProvider(library));

        var catalog = adapter.Create(new CleanupPolicy(
            [
                EpisodeRule("favorite", CleanupRuleTriggerKind.Played, 10, SeriesKeepKind.None) with
                {
                    Filters = Filters(MediaItemKind.Episode) with
                    {
                        FavoriteFilter = RuleFavoriteFilterKind.FavoriteByAnyUser,
                    },
                },
            ],
            AllowDeleteIfPlayedBeforeAdded: false), CancellationToken.None);

        catalog.Items.Single(x => x.Kind == MediaItemKind.Episode)
            .Playback.Single()
            .IsFavorite.Should().BeTrue();
    }

    [Fact]
    public void Create_PopulatesTagsOnlyWhenAnyEnabledRuleNeedsTags()
    {
        var user = CreateUser(0);
        var episode = new Episode
        {
            Id = GuidFrom(30),
            Name = "Episode",
            Path = "/media/show/episode.mkv",
            DateCreated = Now.AddDays(-100),
            Tags = ["cleanup"],
        };
        var libraryManager = new Mock<ILibraryManager>();
        var userData = new CountingUserDataManager([user], [episode]);
        userData.Set(user, episode, PlayedData(Now.AddDays(-30)));
        SetupUsers(libraryManager, [user]);
        SetupLibrary(
            libraryManager,
            [episode],
            new Dictionary<(BaseItemKind Kind, string UserId, ItemSortBy SortBy), int>());
        var adapter = new JellyfinMediaCatalogAdapter(
            NullLogger<JellyfinMediaCatalogAdapter>.Instance,
            CreateUserManager([user]),
            libraryManager.Object,
            userData.Manager,
            new CountingTvHierarchyProvider(TestLibrary.Empty));

        var withoutTags = adapter.Create(new CleanupPolicy(
            [Rule("no-tags", MediaItemKind.Episode, CleanupRuleTriggerKind.Played)],
            AllowDeleteIfPlayedBeforeAdded: false), CancellationToken.None);
        var withTags = adapter.Create(new CleanupPolicy(
            [
                Rule("with-tags", MediaItemKind.Episode, CleanupRuleTriggerKind.Played) with
                {
                    Filters = Filters(MediaItemKind.Episode) with
                    {
                        EnableTagFilter = true,
                        Tags = ["cleanup"],
                    },
                },
            ],
            AllowDeleteIfPlayedBeforeAdded: false), CancellationToken.None);

        withoutTags.Items.Single().Tags.Should().BeEmpty();
        withTags.Items.Single().Tags.Should().ContainSingle("cleanup");
    }

    private static void SetupUsers(Mock<ILibraryManager> libraryManager, IReadOnlyList<JellyfinUser> users)
    {
        libraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) => []);
    }

    private static void SetupLibrary(
        Mock<ILibraryManager> libraryManager,
        TestLibrary library,
        IDictionary<(BaseItemKind Kind, string UserId, ItemSortBy SortBy), int> itemQueries)
    {
        SetupLibrary(libraryManager, library.Episodes, itemQueries);
        libraryManager.Setup(x => x.GetItemById<Season>(It.IsAny<Guid>()))
            .Returns((Guid id) => library.SeasonsById.GetValueOrDefault(id));
        libraryManager.Setup(x => x.GetItemById<Series>(It.IsAny<Guid>()))
            .Returns((Guid id) => library.SeriesById.GetValueOrDefault(id));
    }

    private static void SetupLibrary(
        Mock<ILibraryManager> libraryManager,
        IReadOnlyList<BaseItem> items,
        IDictionary<(BaseItemKind Kind, string UserId, ItemSortBy SortBy), int> itemQueries)
    {
        libraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
            {
                var kind = query.IncludeItemTypes.Single();
                var sortBy = query.OrderBy.Single().Item1;
                var userId = query.User!.Id.ToString("N");
                var key = (kind, userId, sortBy);
                itemQueries[key] = (itemQueries.TryGetValue(key, out var count) ? count : 0) + 1;
                return items.Where(item => IsKind(item, kind)).ToList();
            });
    }

    private static bool IsKind(BaseItem item, BaseItemKind kind) => kind switch
    {
        BaseItemKind.Movie => item is Movie,
        BaseItemKind.Episode => item is Episode,
        _ => false,
    };

    private static IUserManager CreateUserManager(IReadOnlyList<JellyfinUser> users)
    {
        var userManager = new Mock<IUserManager>();
#if JELLYFIN_USER_MANAGER_GET_USERS_METHOD
        userManager.Setup(x => x.GetUsers()).Returns(users);
#else
        userManager.Setup(x => x.Users).Returns(users);
#endif
        return userManager.Object;
    }

    private static JellyfinUser CreateUser(int index)
    {
        var user = new JellyfinUser($"user-{index}", "media-cleaner", "password")
        {
            Id = GuidFrom(1_000 + index),
            Username = $"User {index}",
        };
        return user;
    }

    private static UserItemData PlayedData(DateTime lastPlayed) =>
        new()
        {
            Played = true,
            LastPlayedDate = lastPlayed,
            Key = $"played-{lastPlayed.Ticks}",
        };

    private static CleanupRule EpisodeRule(
        string id,
        CleanupRuleTriggerKind triggerKind,
        int days,
        SeriesKeepKind keepSeriesKind) =>
        Rule(id, MediaItemKind.Episode, triggerKind, days) with
        {
            Filters = Filters(MediaItemKind.Episode) with
            {
                DeleteEpisodes = SeriesDeleteKind.Episode,
                KeepSeriesKind = keepSeriesKind,
            },
        };

    private static CleanupRule Rule(
        string id,
        MediaItemKind kind,
        CleanupRuleTriggerKind triggerKind,
        int days = 10,
        int countAsNotPlayedAfter = -1) =>
        new(
            Id: id,
            Name: id,
            Enabled: true,
            Trigger: new CleanupRuleTrigger(triggerKind, days, CountAsNotPlayedAfter: countAsNotPlayedAfter),
            Filters: Filters(kind),
            Actions: new CleanupRuleActions(CleanupRuleActionKind.Delete, false));

    private static CleanupRuleFilters Filters(MediaItemKind kind) =>
        new(
            MediaKinds: [kind],
            UserIds: [],
            UsersMode: UsersListMode.Ignore,
            FavoriteUserIds: [],
            FavoriteUsersMode: UsersListMode.Ignore,
            FavoriteFilter: RuleFavoriteFilterKind.Ignore,
            Locations: [],
            LocationsMode: LocationsListMode.Exclude,
            EnableTagFilter: false,
            TagFilterMode: TagMode.Exclusion,
            Tags: [],
            DeleteEpisodes: SeriesDeleteKind.Episode,
            KeepSeriesKind: SeriesKeepKind.None);

    private static Guid GuidFrom(int value)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(value).CopyTo(bytes, 0);
        return new Guid(bytes);
    }

    private sealed class CountingUserDataManager
    {
        private readonly Dictionary<(string UserId, string ItemId), UserItemData?> data = new(OrdinalIgnoreCaseUserItemComparer.Instance);
        private readonly Dictionary<(string UserId, string ItemId), int> counts = new(OrdinalIgnoreCaseUserItemComparer.Instance);

        public CountingUserDataManager(IReadOnlyList<JellyfinUser> users, IReadOnlyList<BaseItem> items)
        {
            foreach (var user in users)
            {
                foreach (var item in items)
                {
                    Set(user, item, PlayedData(Now.AddDays(-30)));
                }
            }

            Mock.Setup(x => x.GetUserData(It.IsAny<JellyfinUser>(), It.IsAny<BaseItem>()))
                .Returns((JellyfinUser user, BaseItem item) =>
                {
                    var key = Key(user, item);
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                    data.TryGetValue(key, out var value);
                    return value!;
                });
        }

        public Mock<IUserDataManager> Mock { get; } = new();

        public IUserDataManager Manager => Mock.Object;

        public int TotalCalls => counts.Values.Sum();

        public void Set(JellyfinUser user, BaseItem item, UserItemData? value) =>
            data[Key(user, item)] = value;

        private static (string UserId, string ItemId) Key(JellyfinUser user, BaseItem item) =>
            (user.Id.ToString("N"), item.Id.ToString("N"));
    }

    private sealed class CountingTvHierarchyProvider(TestLibrary library) : IJellyfinTvHierarchyProvider
    {
        public Dictionary<string, int> SeasonEpisodeCalls { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, int> SeriesEpisodeCalls { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, int> SeriesSeasonCalls { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<BaseItem> GetSeasonEpisodes(Season season)
        {
            var key = season.Id.ToString("N");
            SeasonEpisodeCalls[key] = SeasonEpisodeCalls.GetValueOrDefault(key) + 1;
            return library.EpisodesBySeasonId.GetValueOrDefault(season.Id) ?? [];
        }

        public IReadOnlyList<BaseItem> GetSeriesEpisodes(Series series)
        {
            var key = series.Id.ToString("N");
            SeriesEpisodeCalls[key] = SeriesEpisodeCalls.GetValueOrDefault(key) + 1;
            return library.EpisodesBySeriesId.GetValueOrDefault(series.Id) ?? [];
        }

        public IReadOnlyList<BaseItem> GetSeriesSeasons(Series series)
        {
            var key = series.Id.ToString("N");
            SeriesSeasonCalls[key] = SeriesSeasonCalls.GetValueOrDefault(key) + 1;
            return library.SeasonsBySeriesId.GetValueOrDefault(series.Id) ?? [];
        }
    }

    private sealed class TestLibrary
    {
        public static TestLibrary Empty { get; } = new([], [], []);

        private TestLibrary(IReadOnlyList<Series> series, IReadOnlyList<Season> seasons, IReadOnlyList<Episode> episodes)
        {
            Series = series;
            Seasons = seasons;
            Episodes = episodes;
            AllItems = episodes.Cast<BaseItem>().Concat(seasons).Concat(series).ToList();
            SeriesById = series.ToDictionary(x => x.Id);
            SeasonsById = seasons.ToDictionary(x => x.Id);
            EpisodesBySeasonId = episodes.GroupBy(x => x.SeasonId).ToDictionary(x => x.Key, x => x.Cast<BaseItem>().ToList() as IReadOnlyList<BaseItem>);
            EpisodesBySeriesId = episodes.GroupBy(x => x.SeriesId).ToDictionary(x => x.Key, x => x.Cast<BaseItem>().ToList() as IReadOnlyList<BaseItem>);
            SeasonsBySeriesId = seasons.GroupBy(x => x.SeriesId).ToDictionary(x => x.Key, x => x.Cast<BaseItem>().ToList() as IReadOnlyList<BaseItem>);
        }

        public IReadOnlyList<Series> Series { get; }

        public IReadOnlyList<Season> Seasons { get; }

        public IReadOnlyList<Episode> Episodes { get; }

        public IReadOnlyList<BaseItem> AllItems { get; }

        public IReadOnlyDictionary<Guid, Series> SeriesById { get; }

        public IReadOnlyDictionary<Guid, Season> SeasonsById { get; }

        public IReadOnlyDictionary<Guid, IReadOnlyList<BaseItem>> EpisodesBySeasonId { get; }

        public IReadOnlyDictionary<Guid, IReadOnlyList<BaseItem>> EpisodesBySeriesId { get; }

        public IReadOnlyDictionary<Guid, IReadOnlyList<BaseItem>> SeasonsBySeriesId { get; }

        public static TestLibrary Create(ILibraryManager libraryManager, int programCount = ProgramCount, int episodeCount = EpisodeCount)
        {
            BaseItem.LibraryManager = libraryManager;
            var seriesItems = new List<Series>();
            var seasons = new List<Season>();
            var episodes = new List<Episode>();
            for (var seriesIndex = 0; seriesIndex < programCount; seriesIndex++)
            {
                var series = new Series
                {
                    Id = GuidFrom(10_000 + seriesIndex),
                    Name = $"Series {seriesIndex}",
                    Path = $"/media/series-{seriesIndex}",
                    DateCreated = Now.AddDays(-100),
                    Status = SeriesStatus.Ended,
                };
                var season = new Season
                {
                    Id = GuidFrom(20_000 + seriesIndex),
                    Name = "Season 1",
                    SeriesId = series.Id,
                    SeriesName = series.Name,
                    Path = $"{series.Path}/season-1",
                    DateCreated = Now.AddDays(-100),
                    IndexNumber = 1,
                };
                seriesItems.Add(series);
                seasons.Add(season);

                for (var episodeIndex = 0; episodeIndex < GetEpisodeCount(seriesIndex, programCount, episodeCount); episodeIndex++)
                {
                    episodes.Add(new Episode
                    {
                        Id = GuidFrom(30_000 + episodes.Count),
                        Name = $"Episode {episodeIndex}",
                        SeriesId = series.Id,
                        SeasonId = season.Id,
                        SeriesName = series.Name,
                        SeasonName = season.Name,
                        Path = $"{season.Path}/episode-{episodeIndex}.mkv",
                        DateCreated = Now.AddDays(-100),
                        ParentIndexNumber = 1,
                        IndexNumber = episodeIndex + 1,
                    });
                }
            }

            return new TestLibrary(seriesItems, seasons, episodes);
        }

        private static int GetEpisodeCount(int seriesIndex, int programCount, int episodeCount) =>
            episodeCount / programCount + (seriesIndex < episodeCount % programCount ? 1 : 0);
    }

    private sealed class OrdinalIgnoreCaseUserItemComparer : IEqualityComparer<(string UserId, string ItemId)>
    {
        public static OrdinalIgnoreCaseUserItemComparer Instance { get; } = new();

        public bool Equals((string UserId, string ItemId) x, (string UserId, string ItemId) y) =>
            string.Equals(x.UserId, y.UserId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.ItemId, y.ItemId, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string UserId, string ItemId) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.UserId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ItemId));
    }
}
