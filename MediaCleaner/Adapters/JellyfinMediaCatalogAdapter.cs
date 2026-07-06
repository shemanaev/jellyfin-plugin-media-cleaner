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
    IUserDataManager userDataManager) : IMediaCatalogAdapter
{
    public CleanupCatalog Create(CleanupPolicy policy, CancellationToken cancellationToken)
    {
        var jellyfinUsers = JellyfinCompatibility.GetUsers(userManager);
        var users = jellyfinUsers
            .Select(x => new MediaUser(GetUserId(x), x.Username))
            .ToList();
        var usersById = jellyfinUsers.ToDictionary(GetUserId, StringComparer.OrdinalIgnoreCase);

        var snapshot = new SnapshotContext(jellyfinUsers, policy, cancellationToken);
        var itemsById = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
        var mediaItems = new Dictionary<string, MediaItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in CollectItems(policy, jellyfinUsers, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddItem(source.Item, source.Kind, snapshot, itemsById, mediaItems);

            if (source.Item is Episode episode)
            {
                if (episode.Season is not null)
                {
                    AddItem(episode.Season, MediaItemKind.Season, snapshot, itemsById, mediaItems);
                }

                if (episode.Series is not null)
                {
                    AddItem(episode.Series, MediaItemKind.Series, snapshot, itemsById, mediaItems);
                }
            }

            if (source.Item is Season season && season.Series is not null)
            {
                AddItem(season.Series, MediaItemKind.Series, snapshot, itemsById, mediaItems);
            }
        }

        logger.LogDebug("Built cleanup snapshot with {UsersCount} users and {ItemsCount} items", users.Count, mediaItems.Count);
        return new CleanupCatalog(users, mediaItems.Values.ToList(), itemsById, usersById);
    }

    private IEnumerable<CollectedItem> CollectItems(
        CleanupPolicy policy,
        IReadOnlyList<JellyfinUser> users,
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
                    foreach (var item in JellyfinCompatibility.GetUserItemList(libraryManager, source.BaseKind, user, ItemSortBy.DatePlayed))
                    {
                        if (!IsPlayedCandidate(item, user, source.Rule))
                        {
                            continue;
                        }

                        yield return new CollectedItem(item, source.CoreKind);
                    }
                }
                else
                {
                    foreach (var item in JellyfinCompatibility.GetUserItemList(libraryManager, source.BaseKind, user, ItemSortBy.DateCreated))
                    {
                        if (source.Rule.Trigger.Kind == CleanupRuleTriggerKind.NotPlayed
                            && logger.IsEnabled(LogLevel.Trace))
                        {
                            LogNotPlayedCandidate(item, user, policy, source.Rule);
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

    private bool IsPlayedCandidate(BaseItem item, JellyfinUser user, CleanupRule rule)
    {
        var data = userDataManager.GetUserData(user, item);
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
        var tags = GetTags(item);
        var playback = snapshot.Users.Select(user => CreatePlaybackState(user, item)).ToArray();
        var fullName = GetFullName(item);
        var locationPath = GetLocationPath(item, snapshot);
        var series = (item as Episode)?.Series ?? (item as Season)?.Series ?? item as Series;
        var season = (item as Episode)?.Season ?? item as Season;
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
            ? snapshot.GetSeriesEpisodeIds(series)
            : null;
        var seasonOrderIds = kind == MediaItemKind.Episode && series is not null && snapshot.NeedsSeasonOrderIds
            ? snapshot.GetSeriesSeasonIds(series)
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
            SeriesId: series is null ? null : GetItemId(series),
            SeasonId: season is null ? null : GetItemId(season),
            SeriesName: series?.Name ?? (item as Episode)?.SeriesName,
            SeasonName: season?.Name ?? (item as Episode)?.SeasonName,
            ParentIndexNumber: (item as Episode)?.ParentIndexNumber ?? (item as Season)?.IndexNumber,
            IndexNumber: (item as Episode)?.IndexNumber ?? (item as Season)?.IndexNumber,
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

    private PlaybackState CreatePlaybackState(JellyfinUser user, BaseItem item)
    {
        var data = userDataManager.GetUserData(user, item);
        return new PlaybackState(
            UserId: GetUserId(user),
            LastPlayedDate: data?.LastPlayedDate,
            IsPlayed: data?.Played ?? false,
            IsWatching: data?.PlaybackPositionTicks != 0,
            IsFavorite: IsFavorite(user, item, data?.IsFavorite ?? false),
            UserName: user.Username,
            HasUserData: data is not null);
    }

    private void LogPlayedCandidate(BaseItem item, JellyfinUser user)
    {
        var data = userDataManager.GetUserData(user, item);
        var isWatching = data?.PlaybackPositionTicks != 0;
        if (data is null || (!data.Played && !isWatching) || !data.LastPlayedDate.HasValue)
        {
            return;
        }

        logger.LogDebug("\"{Name}\" played by \"{Username}\" ({LastPlayedDate})", GetFullName(item), user.Username, data.LastPlayedDate.Value);
    }

    private void LogNotPlayedCandidate(BaseItem item, JellyfinUser user, CleanupPolicy policy, CleanupRule rule)
    {
        var data = userDataManager.GetUserData(user, item);
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

    private bool IsFavorite(JellyfinUser user, BaseItem item, bool itemIsFavorite) => item switch
    {
        Episode episode => itemIsFavorite
            || (episode.Season is not null && (userDataManager.GetUserData(user, episode.Season)?.IsFavorite ?? false))
            || (episode.Series is not null && (userDataManager.GetUserData(user, episode.Series)?.IsFavorite ?? false)),
        Season season => itemIsFavorite
            || (season.Series is not null && (userDataManager.GetUserData(user, season.Series)?.IsFavorite ?? false)),
        _ => itemIsFavorite,
    };

    private static IReadOnlyList<string> GetTags(BaseItem item)
    {
        var itemTags = item.Tags;
        if ((itemTags is null || !itemTags.Any()) && item is not Episode)
        {
            return Array.Empty<string>();
        }

        var tags = new HashSet<string>(itemTags ?? [], StringComparer.Ordinal);
        if (item is Episode episode)
        {
            foreach (var tag in episode.Season?.Tags ?? [])
            {
                tags.Add(tag);
            }

            foreach (var tag in episode.Series?.Tags ?? [])
            {
                tags.Add(tag);
            }
        }

        return tags.Count == 0 ? Array.Empty<string>() : tags.ToArray();
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
        private readonly SnapshotListCache<BaseItem> seasonEpisodes;
        private readonly SnapshotListCache<BaseItem> seriesEpisodes;
        private readonly SnapshotListCache<BaseItem> seriesSeasons;
        private readonly Dictionary<string, IReadOnlyList<string>> seasonEpisodeIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<string>> seriesEpisodeIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<string>> seriesSeasonIds = new(StringComparer.OrdinalIgnoreCase);

        public SnapshotContext(IReadOnlyList<JellyfinUser> users, CleanupPolicy policy, CancellationToken cancellationToken)
        {
            Users = users;
            this.cancellationToken = cancellationToken;
            seasonEpisodes = new SnapshotListCache<BaseItem>(GetItemId, cancellationToken);
            seriesEpisodes = new SnapshotListCache<BaseItem>(GetItemId, cancellationToken);
            seriesSeasons = new SnapshotListCache<BaseItem>(GetItemId, cancellationToken);
            var enabledEpisodeRules = policy.Rules
                .Where(rule => rule.Enabled && rule.Trigger.Days >= 0 && rule.Filters.MediaKinds.Contains(MediaItemKind.Episode))
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
            NeedsContainerLocationPath = policy.Rules.Any(rule => rule.Enabled && rule.Filters.Locations.Count > 0);
        }

        private readonly CancellationToken cancellationToken;

        public IReadOnlyList<JellyfinUser> Users { get; }

        public bool NeedsSeasonEpisodeIds { get; }

        public bool NeedsSeriesEpisodeIds { get; }

        public bool NeedsSeriesSeasonIds { get; }

        public bool NeedsEpisodeOrderIds { get; }

        public bool NeedsSeasonOrderIds { get; }

        public bool NeedsContainerLocationPath { get; }

        public IReadOnlyList<BaseItem> GetSeasonEpisodes(Season season) =>
            seasonEpisodes.GetOrAdd(
                season,
                () => season.GetEpisodes()
                    .Where(x => !x.IsVirtualItem)
                    .Cast<BaseItem>()
                    .ToList());

        public IReadOnlyList<BaseItem> GetSeriesEpisodes(Series series) =>
            seriesEpisodes.GetOrAdd(
                series,
                () => JellyfinCompatibility.GetEpisodes(series)
                    .Where(x => !x.IsVirtualItem)
                    .Cast<BaseItem>()
                    .ToList());

        public IReadOnlyList<BaseItem> GetSeriesSeasons(Series series) =>
            seriesSeasons.GetOrAdd(
                series,
                () => series.GetSeasons(null, new DtoOptions())
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
    }
}
