using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace MediaCleaner;

internal class ItemsAdapter
{
    private readonly ILogger<ItemsAdapter> _logger;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;

    public ItemsAdapter(
        ILogger<ItemsAdapter> logger,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
    }

    public IEnumerable<ExpiredItem> GetPlayedItems(
        BaseItemKind kind,
        User user,
        CancellationToken cancellationToken)
    {
        var result = new List<ExpiredItem>();
        var items = GetUserItems(kind, user);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var userData = _userDataManager.GetUserData(user, item);

            var isWatching = userData.PlaybackPositionTicks != 0;
            if (!userData.Played && !isWatching) continue;

            if (!userData.LastPlayedDate.HasValue)
            {
                _logger.LogWarning("\"{Name}\" ({Id}) marked as played but has not date", item.Name, item.Id);
                continue;
            }

            if (userData.LastPlayedDate < item.DateCreated)
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
        IEnumerable<Guid> excludeIds,
        CancellationToken cancellationToken)
    {
        var result = new List<ExpiredItem>();
        var items = GetItems(kind, excludeIds);
        foreach ( var item in items )
        {
            cancellationToken.ThrowIfCancellationRequested();

            result.Add(new ExpiredItem
            {
                Item = item,
                Kind = ExpiredKind.NotPlayed,
            });
        }

        return result;
    }

    private IEnumerable<BaseItem> GetItems(BaseItemKind kind, IEnumerable<Guid> excludeIds) =>
        _libraryManager.GetItemList(
                new InternalItemsQuery()
                {
                    ExcludeItemIds = excludeIds.ToArray(),
                    IncludeItemTypes = new[]
                    {
                        kind,
                    },
                    IsVirtualItem = false,
                    OrderBy = new[] { (ItemSortBy.DateCreated, SortOrder.Descending) }
                });

    private IEnumerable<BaseItem> GetUserItems(BaseItemKind kind, User user) =>
        _libraryManager.GetItemList(
                new InternalItemsQuery(user)
                {
                    IncludeItemTypes = new[]
                    {
                        kind,
                    },
                    IsVirtualItem = false,
                    OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) }
                });
}
