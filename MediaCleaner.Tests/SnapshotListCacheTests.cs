using FluentAssertions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaCleaner.Adapters;
using MediaCleaner.Core;
using Moq;

#if JELLYFIN_USER_IN_DATA_ENTITIES
using JellyfinUser = Jellyfin.Data.Entities.User;
#else
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;
#endif

namespace MediaCleaner.Tests;

public class SnapshotListCacheTests
{
    private const int ProgramCount = 72;
    private const int EpisodeCount = 3253;

    [Fact]
    public void GetOrAdd_CachesIssueScaleTvHierarchyLookupsByOwnerId()
    {
        var lookupCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var cache = new SnapshotListCache<Node>(node => node.Id, CancellationToken.None);

        for (var seriesIndex = 0; seriesIndex < ProgramCount; seriesIndex++)
        {
            var series = new Node($"series-{seriesIndex}");
            for (var episodeIndex = 0; episodeIndex < GetEpisodeCount(seriesIndex); episodeIndex++)
            {
                var items = cache.GetOrAdd(series, () =>
                {
                    lookupCounts[series.Id] = lookupCounts.GetValueOrDefault(series.Id) + 1;
                    return Enumerable.Range(0, GetEpisodeCount(seriesIndex))
                        .Select(x => new Node($"episode-{seriesIndex}-{x}"))
                        .ToList();
                });

                items.Should().HaveCount(GetEpisodeCount(seriesIndex));
            }
        }

        lookupCounts.Should().HaveCount(ProgramCount);
        lookupCounts.Values.Should().OnlyContain(x => x == 1);
        lookupCounts.Values.Sum().Should().Be(ProgramCount);
    }

    [Fact]
    public void GetOrAdd_ObservesCancellationBeforeLookup()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cache = new SnapshotListCache<Node>(node => node.Id, cts.Token);

        FluentActions.Invoking(() => cache.GetOrAdd(new Node("series"), () => []))
            .Should()
            .Throw<OperationCanceledException>();
    }

    [Fact]
    public void SnapshotContext_KeepsParentCascadeListsForIndividualEpisodeCleanup()
    {
        var policy = new CleanupPolicy(
            [
                Rule(MediaItemKind.Episode, SeriesDeleteKind.Episode),
            ],
            AllowDeleteIfPlayedBeforeAdded: false);

        var context = CreateSnapshotContext(policy);

        GetFlag(context, "NeedsSeasonEpisodeIds").Should().BeTrue();
        GetFlag(context, "NeedsSeriesEpisodeIds").Should().BeTrue();
        GetFlag(context, "NeedsSeriesSeasonIds").Should().BeTrue();
        GetFlag(context, "NeedsEpisodeOrderIds").Should().BeFalse();
        GetFlag(context, "NeedsSeasonOrderIds").Should().BeFalse();
    }

    private static int GetEpisodeCount(int seriesIndex) =>
        EpisodeCount / ProgramCount + (seriesIndex < EpisodeCount % ProgramCount ? 1 : 0);

    private static object CreateSnapshotContext(CleanupPolicy policy)
    {
        var type = typeof(JellyfinMediaCatalogAdapter).GetNestedType("SnapshotContext", System.Reflection.BindingFlags.NonPublic)!;
        return Activator.CreateInstance(
            type,
            [
                new List<JellyfinUser>(),
                policy,
                Mock.Of<ILibraryManager>(),
                Mock.Of<IUserDataManager>(),
                new EmptyTvHierarchyProvider(),
                CancellationToken.None,
            ])!;
    }

    private static bool GetFlag(object context, string propertyName) =>
        (bool)context.GetType().GetProperty(propertyName)!.GetValue(context)!;

    private static CleanupRule Rule(MediaItemKind mediaKind, SeriesDeleteKind deleteEpisodes) => new(
        Id: $"{mediaKind}-{deleteEpisodes}",
        Name: $"{mediaKind} {deleteEpisodes}",
        Enabled: true,
        Trigger: new CleanupRuleTrigger(CleanupRuleTriggerKind.Played, 10),
        Filters: new CleanupRuleFilters(
            MediaKinds: [mediaKind],
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
            DeleteEpisodes: deleteEpisodes,
            KeepSeriesKind: SeriesKeepKind.None),
        Actions: new CleanupRuleActions(CleanupRuleActionKind.Delete, false));

    private sealed record Node(string Id);

    private sealed class EmptyTvHierarchyProvider : IJellyfinTvHierarchyProvider
    {
        public IReadOnlyList<BaseItem> GetSeasonEpisodes(Season season) => [];

        public IReadOnlyList<BaseItem> GetSeriesEpisodes(Series series) => [];

        public IReadOnlyList<BaseItem> GetSeriesSeasons(Series series) => [];
    }
}
