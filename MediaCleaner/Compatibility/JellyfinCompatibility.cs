using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;

namespace MediaCleaner.Compatibility;

internal static class JellyfinCompatibility
{
    public static IReadOnlyList<JellyfinUser> GetUsers(IUserManager userManager)
    {
#if JELLYFIN_USER_MANAGER_GET_USERS_METHOD
        return [.. userManager.GetUsers()];
#else
        return [.. userManager.Users];
#endif
    }

    public static TaskTriggerInfo CreateIntervalTrigger(TimeSpan interval)
    {
        return new TaskTriggerInfo
        {
#if JELLYFIN_10_10
            Type = TaskTriggerInfo.TriggerInterval,
#else
            Type = TaskTriggerInfoType.IntervalTrigger,
#endif
            IntervalTicks = interval.Ticks,
        };
    }

    public static JellyfinActivityLog CreateActivityLog(
        string title,
        string type,
        Guid userId,
        string shortOverview,
        string overview) =>
        new(title, type, userId)
        {
            ShortOverview = shortOverview,
            Overview = overview,
        };

    public static List<BaseItem> GetItemList(ILibraryManager libraryManager, InternalItemsQuery query) =>
        [.. libraryManager.GetItemList(query)];

    public static List<BaseItem> GetUserItemList(
        ILibraryManager libraryManager,
        BaseItemKind kind,
        JellyfinUser user,
        ItemSortBy sortBy) =>
        GetItemList(
            libraryManager,
            new InternalItemsQuery(user)
            {
                IncludeItemTypes =
                [
                    kind,
                ],
                IsVirtualItem = false,
                OrderBy =
                [
                    (sortBy, JellyfinSortOrder.Descending),
                ],
            });

    public static IEnumerable<BaseItem> GetEpisodes(Series series) =>
        series.GetSeasons(null, new DtoOptions(true)).SelectMany(x => ((Season)x).GetEpisodes());

    public static void DeleteItem(ILibraryManager libraryManager, BaseItem item, DeleteOptions options) =>
        libraryManager.DeleteItem(item, options, true);

    public static void MarkUnplayed(BaseItem item, JellyfinUser user) =>
        item.MarkUnplayed(user);

    public static Task UpdateMetadataAsync(ILibraryManager libraryManager, BaseItem item, CancellationToken cancellationToken) =>
        libraryManager.UpdateItemAsync(item, item, ItemUpdateType.MetadataEdit, cancellationToken);
}
