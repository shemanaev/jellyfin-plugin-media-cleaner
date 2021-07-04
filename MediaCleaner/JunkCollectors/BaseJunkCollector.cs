using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using MediaCleaner.Configuration;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.JunkCollectors
{
    internal abstract class BaseJunkCollector : IJunkCollector
    {
        protected readonly ILogger _logger;
        protected readonly IUserDataManager _userDataManager;
        private readonly ILibraryManager _libraryManager;

        protected BaseJunkCollector(
            ILogger logger,
            ILibraryManager libraryManager,
            IUserDataManager userDataManager)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _userDataManager = userDataManager;
        }

        public abstract List<ExpiredItem> Execute(
            List<User> users,
            CancellationToken cancellationToken);

        protected List<ExpiredItem> GetExpiredItems<T>(
            User user,
            int keepFor,
            CancellationToken cancellationToken)
            where T : BaseItem
        {
            var result = new List<ExpiredItem>();
            var items = GetUserItems<T>(user);
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var userData = _userDataManager.GetUserData(user, item);

                if (!userData.Played || !userData.LastPlayedDate.HasValue) continue;

                var expirationTime = userData.LastPlayedDate.Value.AddDays(keepFor);
                if (DateTime.Now.CompareTo(expirationTime) < 0) continue;

                result.Add(new ExpiredItem {Item = item, User = user, LastPlayedDate = userData.LastPlayedDate.Value});

                _logger.LogDebug("\"{Name}\" has expired for {Username} ({Value})",
                    item.Name, user.Username, userData.LastPlayedDate.Value);
            }

            return result;
        }

        protected List<T> GetUserItems<T>(User user) where T : BaseItem =>
            _libraryManager.GetItemList(
                    new InternalItemsQuery(user)
                    {
                        IncludeItemTypes = new[]
                        {
                            typeof(T).Name
                        },
                        IsVirtualItem = false,
                        OrderBy = new[] {(ItemSortBy.DatePlayed, SortOrder.Descending)}
                    })
            .Cast<T>().ToList();

        protected List<ExpiredItem> FilterFavorites(FavoriteKeepKind kind, List<ExpiredItem> items, List<User> users) => kind switch
        {
            FavoriteKeepKind.AnyUser => items.Where(x => !users.Any(m => IsFavorite(m, x.Item))).ToList(),
            FavoriteKeepKind.AllUsers => items.Where(x => !users.All(m => IsFavorite(m, x.Item))).ToList(),
            FavoriteKeepKind.DontKeep => items,
            _ => throw new NotSupportedException()
        };

        private bool IsFavorite(User user, BaseItem item) => item switch
        {
            Episode episode => _userDataManager.GetUserData(user, episode).IsFavorite
                            || _userDataManager.GetUserData(user, episode.Series).IsFavorite
                            || (episode.Season != null && _userDataManager.GetUserData(user, episode.Season).IsFavorite),

            Movie movie => _userDataManager.GetUserData(user, movie).IsFavorite,

            _ => false
        };
    }
}
