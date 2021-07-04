using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaCleaner.Configuration;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.JunkCollectors
{
    internal class SeriesJunkCollector : BaseJunkCollector
    {
        public SeriesJunkCollector(ILogger logger, ILibraryManager libraryManager, IUserDataManager userDataManager)
            : base(logger, libraryManager, userDataManager)
        {
        }

        public override List<ExpiredItem> Execute(List<User> users, CancellationToken cancellationToken)
        {
            var cutoff = Plugin.Instance.Configuration.KeepEpisodesFor;
            var scale = Plugin.Instance.Configuration.DeleteEpisodes;
            var expired = users
                .SelectMany(x => FilterGroup(scale, GetExpiredItems<Episode>(x, cutoff, cancellationToken)))
                .ToList();

            _logger.LogDebug("{Count} episodes before filtering", expired.Count);
            var filtered = FilterFavorites(Plugin.Instance.Configuration.KeepFavoriteEpisodes, expired, users);
            //filtered = FilterGroup(scale, filtered);
            _logger.LogDebug("{Count} episodes after filtering", filtered.Count);
            return filtered;
        }

        private List<ExpiredItem> FilterGroup(SeriesDeleteKind kind, List<ExpiredItem> items)
        {
            var result = new List<ExpiredItem>();
            switch (kind)
            {
                case SeriesDeleteKind.Season:
                    var seasons = items.GroupBy(x => ((Episode)x.Item).SeasonId);
                    foreach (var season in seasons)
                    {
                        var first = season.OrderByDescending(x => x.LastPlayedDate).FirstOrDefault();
                        if (first?.Item is not Episode episode) continue;
                        var episodes = episode.Season.GetEpisodes();
                        var allWatched = season.Count() == episodes.Count && season.All(value => episodes.Contains(value.Item));
                        if (allWatched) result.Add(new ExpiredItem
                        {
                            Item = episode.Season,
                            User = first.User,
                            LastPlayedDate = first.LastPlayedDate
                        });
                        _logger.LogDebug("'{User}' has watched episodes {Count} of {Total} in season {SeasonName}",
                            season.First().User.Username, episodes.Count, season.Count(), episode.Season.Name);
                    }

                    break;

                case SeriesDeleteKind.Series:
                    var series = items.GroupBy(x => ((Episode)x.Item).SeriesId);
                    foreach (var show in series)
                    {
                        var first = show.OrderByDescending(x => x.LastPlayedDate).FirstOrDefault();
                        if (first?.Item is not Episode episode) continue;
                        var episodes = episode.Series.GetEpisodes().ToList();
                        var allWatched = show.Count() == episodes.Count && show.All(value => episodes.Contains(value.Item));
                        if (allWatched) result.Add(new ExpiredItem
                        {
                            Item = episode.Series,
                            User = first.User,
                            LastPlayedDate = first.LastPlayedDate
                        });
                        _logger.LogDebug("'{User}' has watched episodes {Count} of {Total} in series {SeriesName}",
                            show.First().User.Username, episodes.Count, show.Count(), episode.Series.Name);
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
