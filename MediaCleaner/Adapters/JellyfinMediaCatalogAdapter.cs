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

        var itemsById = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
        var mediaItems = new Dictionary<string, MediaItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in CollectItems(policy, jellyfinUsers, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddItem(source.Item, source.Kind, jellyfinUsers, itemsById, mediaItems);

            if (source.Item is Episode episode)
            {
                if (episode.Season is not null)
                {
                    AddItem(episode.Season, MediaItemKind.Season, jellyfinUsers, itemsById, mediaItems);
                }

                if (episode.Series is not null)
                {
                    AddItem(episode.Series, MediaItemKind.Series, jellyfinUsers, itemsById, mediaItems);
                }
            }

            if (source.Item is Season season && season.Series is not null)
            {
                AddItem(season.Series, MediaItemKind.Series, jellyfinUsers, itemsById, mediaItems);
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
            foreach (var user in users)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (source.Rule.Trigger.Kind == CleanupRuleTriggerKind.Played)
                {
                    foreach (var item in JellyfinCompatibility.GetUserItemList(libraryManager, source.BaseKind, user, ItemSortBy.DatePlayed))
                    {
                        LogPlayedCandidate(item, user);
                        yield return new CollectedItem(item, source.CoreKind);
                    }
                }
                else
                {
                    foreach (var item in JellyfinCompatibility.GetUserItemList(libraryManager, source.BaseKind, user, ItemSortBy.DateCreated))
                    {
                        if (source.Rule.Trigger.Kind == CleanupRuleTriggerKind.NotPlayed)
                        {
                            LogNotPlayedCandidate(item, user, policy, source.Rule);
                        }

                        yield return new CollectedItem(item, source.CoreKind);
                    }
                }
            }
        }
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
        IReadOnlyList<JellyfinUser> users,
        Dictionary<string, BaseItem> itemsById,
        Dictionary<string, MediaItem> mediaItems)
    {
        var id = GetItemId(item);
        if (mediaItems.ContainsKey(id))
        {
            return;
        }

        itemsById[id] = item;
        mediaItems[id] = CreateMediaItem(item, kind, users);
    }

    private MediaItem CreateMediaItem(BaseItem item, MediaItemKind kind, IReadOnlyList<JellyfinUser> users)
    {
        var tags = GetTags(item);
        var playback = users.Select(user => CreatePlaybackState(user, item)).ToList();
        var fullName = GetFullName(item);
        var locationPath = GetLocationPath(item);
        var series = (item as Episode)?.Series ?? (item as Season)?.Series ?? item as Series;
        var season = (item as Episode)?.Season ?? item as Season;
        var seasonEpisodes = season?.GetEpisodes().Where(x => !x.IsVirtualItem).Select(GetItemId).ToList();
        var seriesEpisodes = series?.GetEpisodes().Where(x => !x.IsVirtualItem).Select(GetItemId).ToList();
        var seasons = series?.GetSeasons(null, new DtoOptions()).Select(GetItemId).ToList();

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
                MediaItemKind.Season => seasonEpisodes,
                MediaItemKind.Series => seriesEpisodes,
                _ => null,
            },
            SeasonEpisodeIds: seasonEpisodes,
            SeriesEpisodeIds: seriesEpisodes,
            SeasonIds: seasons,
            FirstEpisodeId: seriesEpisodes?.FirstOrDefault(),
            LastEpisodeId: seriesEpisodes?.LastOrDefault(),
            FirstSeasonId: seasons?.FirstOrDefault(),
            LastSeasonId: seasons?.LastOrDefault());
    }

    private PlaybackState CreatePlaybackState(JellyfinUser user, BaseItem item)
    {
        var data = userDataManager.GetUserData(user, item);
        return new PlaybackState(
            UserId: GetUserId(user),
            LastPlayedDate: data?.LastPlayedDate,
            IsPlayed: data?.Played ?? false,
            IsWatching: data?.PlaybackPositionTicks != 0,
            IsFavorite: IsFavorite(user, item),
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

    private bool IsFavorite(JellyfinUser user, BaseItem item) => item switch
    {
        Episode episode => (userDataManager.GetUserData(user, episode)?.IsFavorite ?? false)
            || (episode.Season is not null && (userDataManager.GetUserData(user, episode.Season)?.IsFavorite ?? false))
            || (episode.Series is not null && (userDataManager.GetUserData(user, episode.Series)?.IsFavorite ?? false)),
        Season season => (userDataManager.GetUserData(user, season)?.IsFavorite ?? false)
            || (season.Series is not null && (userDataManager.GetUserData(user, season.Series)?.IsFavorite ?? false)),
        _ => userDataManager.GetUserData(user, item)?.IsFavorite ?? false,
    };

    private static IReadOnlyList<string> GetTags(BaseItem item)
    {
        var tags = new HashSet<string>(item.Tags ?? [], StringComparer.Ordinal);
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

        return tags.ToList();
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

    private static string? GetLocationPath(BaseItem item) => item switch
    {
        Episode episode => episode.Path,
        Season season => season.GetEpisodes().FirstOrDefault(x => !x.IsVirtualItem)?.Path,
        Series series => series.GetEpisodes().FirstOrDefault(x => !x.IsVirtualItem)?.Path,
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
}
