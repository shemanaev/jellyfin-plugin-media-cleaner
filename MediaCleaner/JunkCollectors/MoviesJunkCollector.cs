using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.JunkCollectors
{
    internal class MoviesJunkCollector : BaseJunkCollector
    {
        public MoviesJunkCollector(ILogger logger, ILibraryManager libraryManager, IUserDataManager userDataManager, IFileSystem fileSystem)
            : base(logger, libraryManager, userDataManager, fileSystem)
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
            filtered = FilterExcludedLocations(filtered, Plugin.Instance.Configuration.LocationsExcluded, Plugin.Instance.Configuration.LocationsMode);
            _logger.LogDebug("{Count} movies after filtering", filtered.Count);
            return filtered;
        }
    }
}
