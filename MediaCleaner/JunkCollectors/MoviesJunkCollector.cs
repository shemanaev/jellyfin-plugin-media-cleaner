using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.JunkCollectors
{
    internal class MoviesJunkCollector : BaseJunkCollector
    {
        public MoviesJunkCollector(ILogger logger, ILibraryManager libraryManager, IUserDataManager userDataManager)
            : base(logger, libraryManager, userDataManager)
        {
        }

        public override List<ExpiredItem> Execute(List<User> users, List<User> usersWithFavorites, CancellationToken cancellationToken)
        {
            var cutoff = Plugin.Instance.Configuration.KeepMoviesFor;
            var expired = users
                .SelectMany(x => GetExpiredItems<Movie>(x, cutoff, cancellationToken))
                .ToList();

            _logger.LogDebug("{Count} movies before filtering", expired.Count);
            var filtered = FilterFavorites(Plugin.Instance.Configuration.KeepFavoriteMovies, expired, usersWithFavorites);
            _logger.LogDebug("{Count} movies after filtering", filtered.Count);
            return filtered;
        }
    }
}
