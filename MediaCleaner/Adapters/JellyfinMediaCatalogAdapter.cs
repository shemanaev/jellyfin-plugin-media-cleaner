using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaCleaner.Compatibility;
using MediaCleaner.Core;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.Adapters;

internal sealed class JellyfinMediaCatalogAdapter(
    ILogger<JellyfinMediaCatalogAdapter> logger,
    IUserManager userManager,
    ILibraryManager libraryManager,
    IUserDataManager userDataManager,
    IJellyfinTvHierarchyProvider? tvHierarchyProvider = null) : IMediaCatalogAdapter
{
    private readonly IJellyfinTvHierarchyProvider tvHierarchyProvider = tvHierarchyProvider ?? new JellyfinTvHierarchyProvider();

    public CleanupCatalog Create(CleanupPolicy policy, CancellationToken cancellationToken)
    {
        var jellyfinUsers = JellyfinCompatibility.GetUsers(userManager);
        var users = jellyfinUsers
            .Select(x => new MediaUser(GetUserId(x), x.Username))
            .ToList();
        var usersById = jellyfinUsers.ToDictionary(GetUserId, StringComparer.OrdinalIgnoreCase);

        var snapshot = new SnapshotContext(
            jellyfinUsers,
            policy,
            libraryManager,
            userDataManager,
            tvHierarchyProvider,
            cancellationToken);
        var itemsById = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
        var mediaItems = new Dictionary<string, MediaItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in CollectItems(policy, jellyfinUsers, snapshot, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddItem(source.Item, source.Kind, snapshot, itemsById, mediaItems);

            if (source.Item is Episode episode)
            {
                var season = snapshot.GetEpisodeSeason(episode);
                if (season is not null)
                {
                    AddItem(season, MediaItemKind.Season, snapshot, itemsById, mediaItems);
                }

                var series = snapshot.GetEpisodeSeries(episode);
                if (series is not null)
                {
                    AddItem(series, MediaItemKind.Series, snapshot, itemsById, mediaItems);
                }
            }

            if (source.Item is Season sourceSeason && snapshot.GetSeasonSeries(sourceSeason) is { } sourceSeries)
            {
                AddItem(sourceSeries, MediaItemKind.Series, snapshot, itemsById, mediaItems);
            }
        }

        logger.LogDebug("Built cleanup snapshot with {UsersCount} users and {ItemsCount} items", users.Count, mediaItems.Count);
        return new CleanupCatalog(users, mediaItems.Values.ToList(), itemsById, usersById);
    }

    private IEnumerable<CollectedItem> CollectItems(
        CleanupPolicy policy,
        IReadOnlyList<JellyfinUser> users,
        SnapshotContext snapshot,
        CancellationToken cancellationToken)
    {
        foreach (var source in GetEnabledKinds(policy))
        {
            var sourceUsers = source.Rule.Trigger.Kind is CleanupRuleTriggerKind.Played or CleanupRuleTriggerKind.NotPlayed
                ? FilterUsersForRule(users, source.Rule)
                : users.Take(1).ToList();

            foreach (var user in sourceUsers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (source.Rule.Trigger.Kind == CleanupRuleTriggerKind.Played)
                {
                    foreach (var item in snapshot.GetUserItems(source.BaseKind, user, ItemSortBy.DatePlayed))
                    {
                        if (!IsPlayedCandidate(item, user, source.Rule, snapshot))
                        {
                            continue;
                        }

                        yield return new CollectedItem(item, source.CoreKind);
                    }
                }
                else
                {
                    foreach (var item in snapshot.GetUserItems(source.BaseKind, user, ItemSortBy.DateCreated))
                    {
                        if (source.Rule.Trigger.Kind == CleanupRuleTriggerKind.NotPlayed
                            && logger.IsEnabled(LogLevel.Trace))
                        {
                            LogNotPlayedCandidate(item, user, policy, source.Rule, snapshot);
                        }

                        yield return new CollectedItem(item, source.CoreKind);
                    }
                }
            }
        }
    }

    private static IReadOnlyList<JellyfinUser> FilterUsersForRule(
        IEnumerable<JellyfinUser> users,
        CleanupRule rule)
    {
        var selectedUserIds = rule.Filters.UserIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return users
            .Where(user => selectedUserIds.Contains(GetUserId(user)) switch
            {
                true when rule.Filters.UsersMode == UsersListMode.Ignore => false,
                true when rule.Filters.UsersMode == UsersListMode.Acknowledge => true,
                false when rule.Filters.UsersMode == UsersListMode.Ignore => true,
                false when rule.Filters.UsersMode == UsersListMode.Acknowledge => false,
                _ => throw new NotSupportedException($"Unsupported users list mode: {rule.Filters.UsersMode}"),
            })
            .ToList();
    }

    private bool IsPlayedCandidate(BaseItem item, JellyfinUser user, CleanupRule rule, SnapshotContext snapshot)
    {
        var data = snapshot.GetUserData(user, item);
        var isWatching = data?.PlaybackPositionTicks != 0;
        if (data is null || (!data.Played && !isWatching) || !data.LastPlayedDate.HasValue)
        {
            return false;
        }

        var startDate = rule.Trigger.CountAsNotPlayedAfter >= 0
            ? DateTime.UtcNow.AddDays(-rule.Trigger.CountAsNotPlayedAfter)
            : (DateTime?)null;
        if (startDate is not null && data.LastPlayedDate < startDate)
        {
            return false;
        }

        logger.LogDebug("\"{Name}\" played by \"{Username}\" ({LastPlayedDate})", GetFullName(item), user.Username, data.LastPlayedDate.Value);
        return true;
    }

    private static IEnumerable<EnabledKind> GetEnabledKinds(CleanupPolicy policy)
    {
        foreach (var rule in policy.Rules.Where(x => x.Enabled && x.Trigger.Days >= 0))
        {
            foreach (var kind in rule.Filters.MediaKinds.Distinct())
            {
                if (TryMapBaseKind(kind, out var baseKind))
                {
                    yield return new EnabledKind(baseKind, kind, rule);
                }
            }
        }
    }

    private static bool TryMapBaseKind(MediaItemKind kind, out BaseItemKind baseKind)
    {
        baseKind = kind switch
        {
            MediaItemKind.Movie => BaseItemKind.Movie,
            MediaItemKind.Episode => BaseItemKind.Episode,
            MediaItemKind.Video => BaseItemKind.Video,
            MediaItemKind.Audio => BaseItemKind.Audio,
            MediaItemKind.AudioBook => BaseItemKind.AudioBook,
            _ => default,
        };

        return kind is MediaItemKind.Movie or MediaItemKind.Episode or MediaItemKind.Video or MediaItemKind.Audio or MediaItemKind.AudioBook;
    }

    private void AddItem(
        BaseItem item,
        MediaItemKind kind,
        SnapshotContext snapshot,
        Dictionary<string, BaseItem> itemsById,
        Dictionary<string, MediaItem> mediaItems)
    {
        var id = GetItemId(item);
        if (mediaItems.ContainsKey(id))
        {
            return;
        }

        itemsById[id] = item;
        mediaItems[id] = CreateMediaItem(item, kind, snapshot);
    }

    private MediaItem CreateMediaItem(BaseItem item, MediaItemKind kind, SnapshotContext snapshot)
    {
        var tags = snapshot.NeedsTags ? GetTags(item, snapshot) : Array.Empty<string>();
        var playback = snapshot.Users.Select(user => CreatePlaybackState(user, item, snapshot)).ToArray();
        var fullName = GetFullName(item);
        var locationPath = GetLocationPath(item, snapshot);
        var episode = item as Episode;
        var itemSeason = item as Season;
        var series = episode is not null
            ? snapshot.GetEpisodeSeries(episode)
            : itemSeason is not null
                ? snapshot.GetSeasonSeries(itemSeason)
                : item as Series;
        var season = episode is not null ? snapshot.GetEpisodeSeason(episode) : itemSeason;
        var seasonEpisodeIds = kind == MediaItemKind.Season && season is not null && snapshot.NeedsSeasonEpisodeIds
            ? snapshot.GetSeasonEpisodeIds(season)
            : null;
        var seriesEpisodeIds = kind == MediaItemKind.Series && series is not null && snapshot.NeedsSeriesEpisodeIds
            ? snapshot.GetSeriesEpisodeIds(series)
            : null;
        var seasonIds = kind == MediaItemKind.Series && series is not null && snapshot.NeedsSeriesSeasonIds
            ? snapshot.GetSeriesSeasonIds(series)
            : null;
        var episodeOrderIds = kind == MediaItemKind.Episode && series is not null && snapshot.NeedsEpisodeOrderIds
            ? snapshot.GetSeriesEpisodeOrderIds(series)
            : null;
        var seasonOrderIds = kind == MediaItemKind.Episode && series is not null && snapshot.NeedsSeasonOrderIds
            ? snapshot.GetSeriesSeasonOrderIds(series)
            : null;

        return new MediaItem(
            Id: GetItemId(item),
            Kind: kind,
            Name: item.Name,
            FullName: fullName,
            DateCreated: item.DateCreated,
            Path: item.Path,
            LocationPath: locationPath,
            Tags: tags,
            Playback: playback,
            SeriesId: GetSeriesId(episode, itemSeason, series),
            SeasonId: GetSeasonId(episode, season),
            SeriesName: series?.Name ?? episode?.SeriesName ?? itemSeason?.SeriesName,
            SeasonName: season?.Name ?? episode?.SeasonName,
            ParentIndexNumber: episode?.ParentIndexNumber ?? itemSeason?.IndexNumber,
            IndexNumber: episode?.IndexNumber ?? itemSeason?.IndexNumber,
            IsVirtual: item.IsVirtualItem,
            SeriesStatus: MapSeriesStatus(series),
            EpisodeIds: kind switch
            {
                MediaItemKind.Season => seasonEpisodeIds,
                MediaItemKind.Series => seriesEpisodeIds,
                _ => null,
            },
            SeasonIds: seasonIds,
            FirstEpisodeId: episodeOrderIds?.FirstOrDefault(),
            LastEpisodeId: episodeOrderIds?.LastOrDefault(),
            FirstSeasonId: seasonOrderIds?.FirstOrDefault(),
            LastSeasonId: seasonOrderIds?.LastOrDefault());
    }

    private PlaybackState CreatePlaybackState(JellyfinUser user, BaseItem item, SnapshotContext snapshot)
    {
        var data = snapshot.GetUserData(user, item);
        return new PlaybackState(
            UserId: GetUserId(user),
            LastPlayedDate: data?.LastPlayedDate,
            IsPlayed: data?.Played ?? false,
            IsWatching: data?.PlaybackPositionTicks != 0,
            IsFavorite: snapshot.NeedsFavoriteState && IsFavorite(user, item, data?.IsFavorite ?? false, snapshot),
            UserName: user.Username,
            HasUserData: data is not null);
    }

    private void LogNotPlayedCandidate(
        BaseItem item,
        JellyfinUser user,
        CleanupPolicy policy,
        CleanupRule rule,
        SnapshotContext snapshot)
    {
        var data = snapshot.GetUserData(user, item);
        if (data is null)
        {
            return;
        }

        var isWatching = data.PlaybackPositionTicks != 0;
        var isPlayedAfterItemCreated = policy.AllowDeleteIfPlayedBeforeAdded || data.LastPlayedDate >= item.DateCreated;
        var shouldSkip = (data.Played && isPlayedAfterItemCreated) || isWatching;
        var startDate = rule.Trigger.CountAsNotPlayedAfter >= 0
            ? DateTime.UtcNow.AddDays(-rule.Trigger.CountAsNotPlayedAfter)
            : (DateTime?)null;

        if (startDate is not null)
        {
            if (shouldSkip && data.LastPlayedDate >= startDate)
            {
                logger.LogTrace("\"{Name}\" ({Id}) was played by {Username} after {StartDate}", item.Name, item.Id, user.Username, startDate);
                return;
            }
        }
        else if (shouldSkip)
        {
            logger.LogTrace("\"{Name}\" ({Id}) was played by {Username}", item.Name, item.Id, user.Username);
            return;
        }

        logger.LogTrace("\"{Name}\" ({Id}) added because not played by {Username}", item.Name, item.Id, user.Username);
    }

    private static bool IsFavorite(JellyfinUser user, BaseItem item, bool itemIsFavorite, SnapshotContext snapshot) => item switch
    {
        Episode episode => itemIsFavorite
            || (snapshot.GetEpisodeSeason(episode) is { } season && (snapshot.GetUserData(user, season)?.IsFavorite ?? false))
            || (snapshot.GetEpisodeSeries(episode) is { } series && (snapshot.GetUserData(user, series)?.IsFavorite ?? false)),
        Season season => itemIsFavorite
            || (snapshot.GetSeasonSeries(season) is { } series && (snapshot.GetUserData(user, series)?.IsFavorite ?? false)),
        _ => itemIsFavorite,
    };

    private static IReadOnlyList<string> GetTags(BaseItem item, SnapshotContext snapshot)
    {
        var itemTags = item.Tags;
        if ((itemTags is null || !itemTags.Any()) && item is not Episode)
        {
            return Array.Empty<string>();
        }

        var tags = new HashSet<string>(itemTags ?? [], StringComparer.Ordinal);
        if (item is Episode episode)
        {
            foreach (var tag in snapshot.GetEpisodeSeason(episode)?.Tags ?? [])
            {
                tags.Add(tag);
            }

            foreach (var tag in snapshot.GetEpisodeSeries(episode)?.Tags ?? [])
            {
                tags.Add(tag);
            }
        }

        return tags.Count == 0 ? Array.Empty<string>() : tags.ToArray();
    }

    private static string? GetSeriesId(Episode? episode, Season? season, Series? series)
    {
        if (series is not null)
        {
            return GetItemId(series);
        }

        var id = episode?.SeriesId ?? season?.SeriesId;
        return id is null || id == Guid.Empty ? null : id.Value.ToString("N");
    }

    private static string? GetSeasonId(Episode? episode, Season? season)
    {
        if (season is not null)
        {
            return GetItemId(season);
        }

        return episode is null || episode.SeasonId == Guid.Empty ? null : episode.SeasonId.ToString("N");
    }

    private static string GetFullName(BaseItem item) => item switch
    {
        Movie movie => movie.Name,
        Series series => series.Name,
        Season season => $"{season.SeriesName} | S{season.IndexNumber:D2} | {season.Name}",
        Episode episode => $"{episode.SeriesName} | S{episode.ParentIndexNumber:D2}E{episode.IndexNumber:D2} | {episode.SeasonName} | {episode.Name}",
        Video video => video.Name,
        _ => item.Name,
    };

    private static string? GetLocationPath(BaseItem item, SnapshotContext snapshot) => item switch
    {
        Episode episode => episode.Path,
        Season season => snapshot.NeedsContainerLocationPath
            ? snapshot.GetSeasonEpisodes(season).FirstOrDefault()?.Path
            : season.Path,
        Series series => snapshot.NeedsContainerLocationPath
            ? snapshot.GetSeriesEpisodes(series).FirstOrDefault()?.Path
            : series.Path,
        Movie movie => movie.Path,
        _ => item.Path,
    };

    private static MediaSeriesStatus MapSeriesStatus(Series? series)
    {
        if (series?.Status is null)
        {
            return MediaSeriesStatus.Unknown;
        }

        return string.Equals(series.Status.Value.ToString(), "Ended", StringComparison.OrdinalIgnoreCase)
            ? MediaSeriesStatus.Ended
            : MediaSeriesStatus.Continuing;
    }

    private static string GetItemId(BaseItem item) => item.Id.ToString("N");

    private static string GetUserId(JellyfinUser user) => user.Id.ToString("N");

    private sealed record EnabledKind(BaseItemKind BaseKind, MediaItemKind CoreKind, CleanupRule Rule);

    private sealed record CollectedItem(BaseItem Item, MediaItemKind Kind);

    private sealed class SnapshotContext
    {
        private readonly ILibraryManager libraryManager;
        private readonly IUserDataManager userDataManager;
        private readonly IJellyfinTvHierarchyProvider tvHierarchyProvider;
        private readonly Dictionary<ItemQueryKey, IReadOnlyList<BaseItem>> itemQueries = [];
        private readonly Dictionary<UserDataKey, UserItemData?> userData = [];
        private readonly Dictionary<Guid, Season?> seasonsById = [];
        private readonly Dictionary<Guid, Series?> seriesById = [];
        private readonly SnapshotListCache<BaseItem> seasonEpisodes;
        private readonly SnapshotListCache<BaseItem> seriesEpisodes;
        private readonly SnapshotListCache<BaseItem> seriesSeasons;
        private readonly Dictionary<string, IReadOnlyList<string>> seasonEpisodeIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<string>> seriesEpisodeIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<string>> seriesSeasonIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<string>> seriesEpisodeOrderIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<string>> seriesSeasonOrderIds = new(StringComparer.OrdinalIgnoreCase);

        public SnapshotContext(
            IReadOnlyList<JellyfinUser> users,
            CleanupPolicy policy,
            ILibraryManager libraryManager,
            IUserDataManager userDataManager,
            IJellyfinTvHierarchyProvider tvHierarchyProvider,
            CancellationToken cancellationToken)
        {
            Users = users;
            this.libraryManager = libraryManager;
            this.userDataManager = userDataManager;
            this.tvHierarchyProvider = tvHierarchyProvider;
            this.cancellationToken = cancellationToken;
            seasonEpisodes = new SnapshotListCache<BaseItem>(GetItemId, cancellationToken);
            seriesEpisodes = new SnapshotListCache<BaseItem>(GetItemId, cancellationToken);
            seriesSeasons = new SnapshotListCache<BaseItem>(GetItemId, cancellationToken);
            var enabledRules = policy.Rules
                .Where(rule => rule.Enabled && rule.Trigger.Days >= 0)
                .ToList();
            var enabledEpisodeRules = enabledRules
                .Where(rule => rule.Filters.MediaKinds.Contains(MediaItemKind.Episode))
                .ToList();
            NeedsSeasonEpisodeIds = enabledEpisodeRules.Any(rule => rule.Filters.DeleteEpisodes is SeriesDeleteKind.Episode or SeriesDeleteKind.Season);
            NeedsSeriesEpisodeIds = enabledEpisodeRules.Count > 0;
            NeedsSeriesSeasonIds = enabledEpisodeRules.Count > 0;
            NeedsEpisodeOrderIds = enabledEpisodeRules.Any(rule =>
                rule.Filters.DeleteEpisodes == SeriesDeleteKind.Episode
                && rule.Filters.KeepSeriesKind != SeriesKeepKind.None);
            NeedsSeasonOrderIds = enabledEpisodeRules.Any(rule =>
                rule.Filters.DeleteEpisodes == SeriesDeleteKind.Season
                && rule.Filters.KeepSeriesKind != SeriesKeepKind.None);
            NeedsContainerLocationPath = enabledRules.Any(rule => rule.Filters.Locations.Count > 0);
            NeedsFavoriteState = enabledRules.Any(rule => rule.Filters.FavoriteFilter != RuleFavoriteFilterKind.Ignore);
            NeedsTags = enabledRules.Any(rule => rule.Filters.EnableTagFilter);
        }

        private readonly CancellationToken cancellationToken;

        public IReadOnlyList<JellyfinUser> Users { get; }

        public bool NeedsSeasonEpisodeIds { get; }

        public bool NeedsSeriesEpisodeIds { get; }

        public bool NeedsSeriesSeasonIds { get; }

        public bool NeedsEpisodeOrderIds { get; }

        public bool NeedsSeasonOrderIds { get; }

        public bool NeedsContainerLocationPath { get; }

        public bool NeedsFavoriteState { get; }

        public bool NeedsTags { get; }

        public IReadOnlyList<BaseItem> GetUserItems(BaseItemKind kind, JellyfinUser user, ItemSortBy sortBy)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = new ItemQueryKey(kind, GetUserId(user), sortBy);
            if (itemQueries.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var items = JellyfinCompatibility.GetUserItemList(libraryManager, kind, user, sortBy);
            itemQueries[key] = items;
            return items;
        }

        public UserItemData? GetUserData(JellyfinUser user, BaseItem item)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = new UserDataKey(GetUserId(user), GetItemId(item));
            if (userData.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var data = userDataManager.GetUserData(user, item);
            userData[key] = data;
            return data;
        }

        public Season? GetEpisodeSeason(Episode episode) =>
            episode.Season ?? GetSeasonById(episode.SeasonId);

        public Series? GetEpisodeSeries(Episode episode) =>
            episode.Series ?? GetSeriesById(episode.SeriesId);

        public Series? GetSeasonSeries(Season season) =>
            season.Series ?? GetSeriesById(season.SeriesId);

        private Season? GetSeasonById(Guid id)
        {
            if (id == Guid.Empty)
            {
                return null;
            }

            if (seasonsById.TryGetValue(id, out var cached))
            {
                return cached;
            }

            var season = libraryManager.GetItemById<Season>(id);
            seasonsById[id] = season;
            return season;
        }

        private Series? GetSeriesById(Guid id)
        {
            if (id == Guid.Empty)
            {
                return null;
            }

            if (seriesById.TryGetValue(id, out var cached))
            {
                return cached;
            }

            var series = libraryManager.GetItemById<Series>(id);
            seriesById[id] = series;
            return series;
        }

        public IReadOnlyList<BaseItem> GetSeasonEpisodes(Season season) =>
            seasonEpisodes.GetOrAdd(
                season,
                () => tvHierarchyProvider.GetSeasonEpisodes(season)
                    .Where(x => !x.IsVirtualItem)
                    .Cast<BaseItem>()
                    .ToList());

        public IReadOnlyList<BaseItem> GetSeriesEpisodes(Series series) =>
            seriesEpisodes.GetOrAdd(
                series,
                () => tvHierarchyProvider.GetSeriesEpisodes(series)
                    .Where(x => !x.IsVirtualItem)
                    .Cast<BaseItem>()
                    .ToList());

        public IReadOnlyList<BaseItem> GetSeriesSeasons(Series series) =>
            seriesSeasons.GetOrAdd(
                series,
                () => tvHierarchyProvider.GetSeriesSeasons(series)
                    .Cast<BaseItem>()
                    .ToList());

        public IReadOnlyList<string> GetSeasonEpisodeIds(Season season) =>
            GetOrAddIds(
                seasonEpisodeIds,
                GetItemId(season),
                () => GetSeasonEpisodes(season).Select(GetItemId).ToList());

        public IReadOnlyList<string> GetSeriesEpisodeIds(Series series) =>
            GetOrAddIds(
                seriesEpisodeIds,
                GetItemId(series),
                () => GetSeriesEpisodes(series).Select(GetItemId).ToList());

        public IReadOnlyList<string> GetSeriesSeasonIds(Series series) =>
            GetOrAddIds(
                seriesSeasonIds,
                GetItemId(series),
                () => GetSeriesSeasons(series).Select(GetItemId).ToList());

        public IReadOnlyList<string> GetSeriesEpisodeOrderIds(Series series) =>
            GetOrAddIds(
                seriesEpisodeOrderIds,
                GetItemId(series),
                () =>
                {
                    var items = GetSeriesEpisodes(series);
                    var episodes = items.OfType<Episode>().ToList();
                    if (episodes.Count == 0
                        || episodes.Count != items.Count
                        || episodes.Any(x => !x.ParentIndexNumber.HasValue || !x.IndexNumber.HasValue))
                    {
                        return [];
                    }

                    var ordered = episodes
                        .OrderBy(x => x.ParentIndexNumber!.Value)
                        .ThenBy(x => x.IndexNumber!.Value)
                        .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(GetItemId, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    return HasDuplicateEpisodeNumbers(ordered)
                        ? []
                        : ordered.Select(GetItemId).ToList();
                });

        public IReadOnlyList<string> GetSeriesSeasonOrderIds(Series series) =>
            GetOrAddIds(
                seriesSeasonOrderIds,
                GetItemId(series),
                () =>
                {
                    var items = GetSeriesSeasons(series);
                    var seasons = items.OfType<Season>().ToList();
                    if (seasons.Count == 0
                        || seasons.Count != items.Count
                        || seasons.Any(x => !x.IndexNumber.HasValue))
                    {
                        return [];
                    }

                    var ordered = seasons
                        .OrderBy(x => x.IndexNumber!.Value)
                        .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(GetItemId, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    return ordered.GroupBy(x => x.IndexNumber!.Value).Any(x => x.Count() > 1)
                        ? []
                        : ordered.Select(GetItemId).ToList();
                });

        private static bool HasDuplicateEpisodeNumbers(IEnumerable<Episode> episodes) =>
            episodes
                .GroupBy(x => (Season: x.ParentIndexNumber!.Value, Episode: x.IndexNumber!.Value))
                .Any(x => x.Count() > 1);

        private IReadOnlyList<string> GetOrAddIds(
            Dictionary<string, IReadOnlyList<string>> cache,
            string key,
            Func<IReadOnlyList<string>> factory)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var value = factory();
            cache[key] = value;
            return value;
        }

        private readonly record struct ItemQueryKey(BaseItemKind Kind, string UserId, ItemSortBy SortBy);

        private readonly record struct UserDataKey(string UserId, string ItemId);
    }
}

internal interface IJellyfinTvHierarchyProvider
{
    IReadOnlyList<BaseItem> GetSeasonEpisodes(Season season);

    IReadOnlyList<BaseItem> GetSeriesEpisodes(Series series);

    IReadOnlyList<BaseItem> GetSeriesSeasons(Series series);
}

internal sealed class JellyfinTvHierarchyProvider : IJellyfinTvHierarchyProvider
{
    public IReadOnlyList<BaseItem> GetSeasonEpisodes(Season season) =>
        season.GetEpisodes().Cast<BaseItem>().ToList();

    public IReadOnlyList<BaseItem> GetSeriesEpisodes(Series series) =>
        JellyfinCompatibility.GetEpisodes(series).ToList();

    public IReadOnlyList<BaseItem> GetSeriesSeasons(Series series) =>
        series.GetSeasons(null, new DtoOptions()).Cast<Season>().ToList();
}
