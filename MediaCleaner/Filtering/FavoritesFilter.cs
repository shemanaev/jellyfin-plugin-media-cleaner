using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Entities;
using MediaCleaner.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.Filtering;

internal class FavoritesFilter : IExpiredItemFilter
{
    private readonly ILogger<FavoritesFilter> _logger;
    private readonly FavoriteKeepKind _kind;
    private readonly List<User> _users;
    private readonly IUserDataManager _userDataManager;

    public FavoritesFilter(
        ILogger<FavoritesFilter> logger,
        FavoriteKeepKind kind,
        List<User> users,
        IUserDataManager userDataManager)
    {
        _logger = logger;
        _kind = kind;
        _users = users;
        _userDataManager = userDataManager;
    }

    public string Name => "Favorites";

    public List<ExpiredItem> Apply(List<ExpiredItem> items)
    {
        var result = new List<ExpiredItem>();

        foreach (var item in items)
        {
            switch (_kind)
            {
                case FavoriteKeepKind.DontKeep:
                    result.Add(item);
                    break;

                case FavoriteKeepKind.AnyUser:
                    if (!_users.Any(m => IsFavorite(m, item.Item)))
                    {
                        result.Add(item);
                    }
                    else
                    {
                        var user = _users.First(m => IsFavorite(m, item.Item));
                        _logger.LogTrace("\"{Name}\" is favorited by \"{Username}\"", item.Item.Name, user.Username);
                    }
                    break;

                case FavoriteKeepKind.AllUsers:
                    if (!_users.All(m => IsFavorite(m, item.Item)))
                    {
                        result.Add(item);
                    }
                    break;
            }
        }

        return result;
    }

    private bool IsFavorite(User user, BaseItem item) => item switch
    {
        Episode episode => _userDataManager.GetUserData(user, episode).IsFavorite
                        || (_userDataManager.GetUserData(user, episode.Series)?.IsFavorite ?? false)
                        || (episode.Season != null && (_userDataManager.GetUserData(user, episode.Season)?.IsFavorite ?? false)),

        Season season => _userDataManager.GetUserData(user, season).IsFavorite
                      || (_userDataManager.GetUserData(user, season.Series)?.IsFavorite ?? false), // FIXME: check if any episode favorited?

        Series series => _userDataManager.GetUserData(user, series).IsFavorite, // FIXME: same as season

        Movie movie => _userDataManager.GetUserData(user, movie).IsFavorite,

        _ => _userDataManager.GetUserData(user, item).IsFavorite
    };
}
