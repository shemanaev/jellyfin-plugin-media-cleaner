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

    public List<ExpiredItem> GetPlayedItems(
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

            if (!userData.Played || !userData.LastPlayedDate.HasValue) continue;

            result.Add(new ExpiredItem
            {
                Item = item,
                User = user,
                LastPlayedDate = userData.LastPlayedDate.Value
            });

            _logger.LogDebug("'{Name}' has been played by {Username} ({Value})",
                item.Name, user.Username, userData.LastPlayedDate.Value);
        }

        return result;
    }

    private List<BaseItem> GetUserItems(BaseItemKind kind, User user) =>
        _libraryManager.GetItemList(
                new InternalItemsQuery(user)
                {
                    IncludeItemTypes = new[]
                    {
                        kind,
                    },
                    IsVirtualItem = false,
                    OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) }
                })
        .ToList();
}
