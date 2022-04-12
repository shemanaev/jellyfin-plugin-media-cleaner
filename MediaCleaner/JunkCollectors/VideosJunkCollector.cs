using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.JunkCollectors
{
    internal class VideosJunkCollector : BaseJunkCollector
    {
        public VideosJunkCollector(ILogger logger, ILibraryManager libraryManager, IUserDataManager userDataManager, IFileSystem fileSystem)
            : base(logger, libraryManager, userDataManager, fileSystem)
        {
        }

        public override List<ExpiredItem> Execute(List<User> users, List<User> usersWithFavorites, CancellationToken cancellationToken)
        {
            var cutoff = Plugin.Instance.Configuration.KeepVideosFor;
            var expired = users
                .SelectMany(x => GetExpiredItems<Video>(x, cutoff, cancellationToken))
                .ToList();

            _logger.LogDebug("{Count} videos before filtering", expired.Count);
            var filtered = FilterFavorites(Plugin.Instance.Configuration.KeepFavoriteVideos, expired, usersWithFavorites);
            filtered = FilterExcludedLocations(filtered, Plugin.Instance.Configuration.LocationsExcluded);
            _logger.LogDebug("{Count} videos after filtering", filtered.Count);
            return filtered;
        }
    }
}
