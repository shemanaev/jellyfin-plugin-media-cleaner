using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaCleaner.Configuration;
using Microsoft.Extensions.Logging;
using Jellyfin.Database.Implementations.Enums;

namespace MediaCleaner;

internal class ItemsAdapter
{
    private readonly ILogger<ItemsAdapter> _logger;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly PluginConfiguration _config;

    public ItemsAdapter(
        ILogger<ItemsAdapter> logger,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        PluginConfiguration config)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _config = config;
    }

    public IEnumerable<ExpiredItem> GetPlayedItems(
        BaseItemKind kind,
        User user,
        DateTime? startDate,
        CancellationToken cancellationToken)
    {
        var result = new List<ExpiredItem>();
        var items = GetUserItems(kind, user, ItemSortBy.DatePlayed);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var userData = _userDataManager.GetUserData(user, item);
            if (userData == null) continue;

            var isWatching = userData.PlaybackPositionTicks != 0;
            if (!userData.Played && !isWatching) continue;

            if (!userData.LastPlayedDate.HasValue)
            {
                _logger.LogWarning("\"{Name}\" ({Id}) marked as played but has not date", item.Name, item.Id);
                continue;
            }

            if (startDate != null && userData.LastPlayedDate < startDate)
            {
                _logger.LogTrace("\"{Name}\" ({Id}) was played by {Username} before {StartDate}", item.Name, item.Id, user.Username, startDate);
                continue;
            }

            if (userData.LastPlayedDate < item.DateCreated && !_config.AllowDeleteIfPlayedBeforeAdded)
            {
                _logger.LogWarning("Ignoring \"{Name}\" ({Id}): played by \"{Username}\" at {LastPlayedDate}, but added at {DateCreated}",
                    item.Name, item.Id, user.Username, userData.LastPlayedDate.Value.ToLocalTime(), item.DateCreated.ToLocalTime());
                continue;
            }

            var expiredItem = new ExpiredItem
            {
                Item = item,
                Kind = ExpiredKind.Played,
                Data = new List<ExpiredItemData> {
                    new() {
                        User = user,
                        LastPlayedDate = userData.LastPlayedDate.Value,
                        IsPlayed = userData.Played,
                        IsWatching = isWatching,
                    }
                },
            };

            result.Add(expiredItem);

            _logger.LogDebug("\"{Name}\" played by \"{Username}\" ({LastPlayedDate})",
                expiredItem.FullName, user.Username, userData.LastPlayedDate.Value);
        }

        return result;
    }

    public IEnumerable<ExpiredItem> GetNotPlayedItems(
        BaseItemKind kind,
        User user,
        DateTime? startDate,
        CancellationToken cancellationToken)
    {
        var result = new List<ExpiredItem>();
        var items = GetUserItems(kind, user, ItemSortBy.DateCreated);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var userData = _userDataManager.GetUserData(user, item);
            if (userData == null) continue;

            var isWatching = userData.PlaybackPositionTicks != 0;
            var isPlayedAfterItemCreated = _config.AllowDeleteIfPlayedBeforeAdded || userData.LastPlayedDate >= item.DateCreated;
            var shouldSkip = (userData.Played && isPlayedAfterItemCreated) || isWatching;

            if (startDate != null)
            {
                if (shouldSkip && userData.LastPlayedDate >= startDate)
                {
                    _logger.LogTrace("\"{Name}\" ({Id}) was played by {Username} after {StartDate}", item.Name, item.Id, user.Username, startDate);
                    continue;
                }
            }
            else
            {
                if (shouldSkip)
                {
                    _logger.LogTrace("\"{Name}\" ({Id}) was played by {Username}", item.Name, item.Id, user.Username);
                    continue;
                }
            }

            _logger.LogTrace("\"{Name}\" ({Id}) added because not played by {Username}", item.Name, item.Id, user.Username);
            result.Add(new ExpiredItem
            {
                Item = item,
                Kind = ExpiredKind.NotPlayed,
            });
        }

        return result;
    }

    private List<BaseItem> GetUserItems(BaseItemKind kind, User user, ItemSortBy sortBy) =>
        [.. _libraryManager.GetItemList(
                new InternalItemsQuery(user)
                {
                    IncludeItemTypes =
                    [
                        kind,
                    ],
                    IsVirtualItem = false,
                    OrderBy =
                    [
                        (sortBy, SortOrder.Descending),
                    ]
                })];
}
