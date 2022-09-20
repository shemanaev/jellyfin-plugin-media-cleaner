using System.Collections.Generic;
using System.Linq;
using MediaCleaner.Configuration;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.Filtering;

internal class SeriesFilter : IExpiredItemFilter
{
    private readonly ILogger<SeriesFilter> _logger;
    private readonly SeriesDeleteKind _kind;

    public SeriesFilter(ILogger<SeriesFilter> logger, SeriesDeleteKind kind)
    {
        _logger = logger;
        _kind = kind;
    }

    public string Name => "Series";

    public List<ExpiredItem> Apply(List<ExpiredItem> itemsAll)
    {
        var result = new List<ExpiredItem>();

        // group by user to ensure at least one user fully watched series/season
        foreach (var items in itemsAll.GroupBy(x => x.User))
        {
            switch (_kind)
            {
                case SeriesDeleteKind.Season:
                    var seasons = items.GroupBy(x => ((Episode)x.Item).Season?.Id ?? ((Episode)x.Item).Series?.Id);
                    foreach (var season in seasons)
                    {
                        var first = season.OrderByDescending(x => x.LastPlayedDate).FirstOrDefault();
                        if (first?.Item is not Episode episode) continue;
                        var episodes = episode.Season.GetEpisodes().Where(x => !x.IsVirtualItem).ToList();
                        var allWatched = season.Count() == episodes.Count && season.All(value => episodes.Contains(value.Item));

                        _logger.LogDebug("\"{User}\" has watched episodes {Count} of {Total} in season \"{SeriesName}\": \"{SeasonName}\"",
                            season.First().User.Username, season.Count(), episodes.Count, episode.Series.Name, episode.Season.Name);

                        if (allWatched)
                        {
                            result.Add(new ExpiredItem
                            {
                                Item = episode.Season,
                                User = first.User,
                                LastPlayedDate = first.LastPlayedDate
                            });

                            _logger.LogTrace("Season \"{SeriesName}\": \"{SeasonName}\" was added to expired items", episode.SeriesName, episode.Season.Name);
                        }
                    }

                    break;

                case SeriesDeleteKind.Series:
                case SeriesDeleteKind.SeriesEnded:
                    var series = items.GroupBy(x => ((Episode)x.Item).Series?.Id);
                    foreach (var show in series)
                    {
                        var first = show.OrderByDescending(x => x.LastPlayedDate).FirstOrDefault();
                        if (first?.Item is not Episode episode) continue;
                        var episodes = episode.Series.GetEpisodes().Where(x => !x.IsVirtualItem).ToList();
                        var allWatched = show.Count() == episodes.Count && show.All(value => episodes.Contains(value.Item));

                        _logger.LogDebug("\"{User}\" has watched episodes {Count} of {Total} in series \"{SeriesName}\"'",
                            show.First().User.Username, show.Count(), episodes.Count, episode.Series.Name);

                        if (allWatched)
                        {
                            if (_kind == SeriesDeleteKind.SeriesEnded)
                            {
                                if (episode.Series.Status.HasValue
                                 && episode.Series.Status.Value != MediaBrowser.Model.Entities.SeriesStatus.Ended)
                                {
                                    _logger.LogTrace("Series \"{SeriesName}\" was NOT added to expired items because metadata indicates that it's not yet finished",
                                        episode.SeriesName);
                                    continue;
                                }
                            }

                            result.Add(new ExpiredItem
                            {
                                Item = episode.Series,
                                User = first.User,
                                LastPlayedDate = first.LastPlayedDate
                            });

                            _logger.LogTrace("Series \"{SeriesName}\" was added to expired items", episode.SeriesName);
                        }
                    }

                    break;

                default:
                    result = itemsAll;
                    break;
            }
        }

        return result;
    }
}
