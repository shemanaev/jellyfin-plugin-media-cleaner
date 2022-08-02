using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using MediaCleaner.Configuration;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.JunkCollectors
{
    internal class SeriesJunkCollector : BaseJunkCollector
    {
        public SeriesJunkCollector(ILogger logger, ILibraryManager libraryManager, IUserDataManager userDataManager, IFileSystem fileSystem)
            : base(logger, libraryManager, userDataManager, fileSystem)
        {
        }

        public override List<ExpiredItem> Execute(List<User> users, List<User> usersWithFavorites, CancellationToken cancellationToken)
        {
            var cutoff = Plugin.Instance.Configuration.KeepEpisodesFor;
            var scale = Plugin.Instance.Configuration.DeleteEpisodes;
            var expired = users
                .SelectMany(x => FilterGroup(scale, GetExpiredItems<Episode>(x, cutoff, cancellationToken)))
                .ToList();

            _logger.LogDebug("{Count} episodes before filtering", expired.Count);
            var filtered = FilterFavorites(Plugin.Instance.Configuration.KeepFavoriteEpisodes, expired, usersWithFavorites);
            filtered = FilterExcludedLocations(filtered, Plugin.Instance.Configuration.LocationsExcluded, Plugin.Instance.Configuration.LocationsMode);
            _logger.LogDebug("{Count} episodes after filtering", filtered.Count);
            return filtered;
        }

        private List<ExpiredItem> FilterGroup(SeriesDeleteKind kind, List<ExpiredItem> items)
        {
            var result = new List<ExpiredItem>();
            switch (kind)
            {
                case SeriesDeleteKind.Season:
                    var seasons = items.GroupBy(x => ((Episode)x.Item).Season?.Id ?? ((Episode)x.Item).Series?.Id);
                    foreach (var season in seasons)
                    {
                        var first = season.OrderByDescending(x => x.LastPlayedDate).FirstOrDefault();
                        if (first?.Item is not Episode episode) continue;
                        var episodes = episode.Season.GetEpisodes().Where(x => !x.IsVirtualItem).ToList();
                        var allWatched = season.Count() == episodes.Count && season.All(value => episodes.Contains(value.Item));
                        if (allWatched)
                        {
                            result.Add(new ExpiredItem
                            {
                                Item = episode.Season,
                                User = first.User,
                                LastPlayedDate = first.LastPlayedDate
                            });
                        }

                        _logger.LogDebug("'{User}' has watched episodes {Count} of {Total} in season {SeriesName}: {SeasonName}",
                            season.First().User.Username, season.Count(), episodes.Count, episode.Series.Name, episode.Season.Name);
                    }

                    break;

                case SeriesDeleteKind.Series:
                    var series = items.GroupBy(x => ((Episode)x.Item).Series?.Id);
                    foreach (var show in series)
                    {
                        var first = show.OrderByDescending(x => x.LastPlayedDate).FirstOrDefault();
                        if (first?.Item is not Episode episode) continue;
                        var episodes = episode.Series.GetEpisodes().Where(x => !x.IsVirtualItem).ToList();
                        var allWatched = show.Count() == episodes.Count && show.All(value => episodes.Contains(value.Item));
                        if (allWatched)
                        {
                            result.Add(new ExpiredItem
                            {
                                Item = episode.Series,
                                User = first.User,
                                LastPlayedDate = first.LastPlayedDate
                            });
                        }
                        _logger.LogDebug("'{User}' has watched episodes {Count} of {Total} in series {SeriesName}",
                            show.First().User.Username, show.Count(), episodes.Count, episode.Series.Name);
                    }

                    break;

                default:
                    result = items;
                    break;
            }

            return result;
        }
    }
}
