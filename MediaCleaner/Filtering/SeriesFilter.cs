using System.Collections.Generic;
using System.Linq;
using MediaCleaner.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Model.Entities;
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

    public List<ExpiredItem> Apply(List<ExpiredItem> items)
    {
        var result = new List<ExpiredItem>();

        switch (_kind)
        {
            case SeriesDeleteKind.Season:
            case SeriesDeleteKind.SeasonKeepLast:
                var seasons = items.GroupBy(x => ((Episode)x.Item).Season?.Id ?? ((Episode)x.Item).Series?.Id);
                foreach (var season in seasons)
                {
                    var first = season.MaxBy(x => x.Data?.First()?.LastPlayedDate ?? x.Item.DateCreated);
                    if (first?.Item is not Episode episode) continue;
                    var episodes = episode.Season.GetEpisodes().Where(x => !x.IsVirtualItem).ToList();
                    var allWatched = season.Count() == episodes.Count && season.All(value => episodes.Contains(value.Item));

                    _logger.LogDebug("\"{Username}\" has watched episodes {Count} of {Total} in season \"{SeriesName}\": \"{SeasonName}\"",
                        season.First().Data?.First()?.User.Username ?? "[None]", season.Count(), episodes.Count, episode.Series.Name, episode.Season.Name);

                    if (allWatched)
                    {
                        var expiredItem = new ExpiredItem
                        {
                            Item = episode.Season,
                            Data = first.Data,
                            Kind = first.Kind,
                        };

                        if (_kind == SeriesDeleteKind.SeasonKeepLast)
                        {
                            var seriesStatus = episode.Series.Status;
                            var lastSeasonId = episode.Series.GetSeasons(first.Data?.First()?.User, new DtoOptions()).LastOrDefault()?.Id;
                            if (seriesStatus.HasValue
                                && seriesStatus.Value != SeriesStatus.Ended
                                && episode.SeasonId == lastSeasonId)
                            {
                                _logger.LogTrace("Season \"{SeasonName}\" was NOT added to expired items because this is the last one and metadata indicates that show is continued",
                                    expiredItem.FullName);
                                continue;
                            }
                        }

                        result.Add(expiredItem);

                        _logger.LogTrace("Season \"{SeasonName}\" was added to expired items", expiredItem.FullName);
                    }
                }

                break;

            case SeriesDeleteKind.Series:
            case SeriesDeleteKind.SeriesEnded:
                var series = items.GroupBy(x => ((Episode)x.Item).Series?.Id);
                foreach (var show in series)
                {
                    var first = show.MaxBy(x => x.Data?.First()?.LastPlayedDate ?? x.Item.DateCreated);
                    if (first?.Item is not Episode episode) continue;
                    var episodes = episode.Series.GetEpisodes().Where(x => !x.IsVirtualItem).ToList();
                    var allWatched = show.Count() == episodes.Count && show.All(value => episodes.Contains(value.Item));

                    _logger.LogDebug("\"{Username}\" has watched episodes {Count} of {Total} in series \"{SeriesName}\"'",
                        show.First().Data?.First()?.User.Username ?? "[None]", show.Count(), episodes.Count, episode.Series.Name);

                    if (allWatched)
                    {
                        var expiredItem = new ExpiredItem
                        {
                            Item = episode.Series,
                            Data = first.Data,
                            Kind = first.Kind,
                        };

                        if (_kind == SeriesDeleteKind.SeriesEnded)
                        {
                            var seriesStatus = episode.Series.Status;
                            if (seriesStatus.HasValue
                                && seriesStatus.Value != SeriesStatus.Ended)
                            {
                                _logger.LogTrace("Series \"{SeriesName}\" was NOT added to expired items because metadata indicates that it's not yet finished",
                                    expiredItem.FullName);
                                continue;
                            }
                        }

                        result.Add(expiredItem);

                        _logger.LogTrace("Series \"{SeriesName}\" was added to expired items", episode.SeriesName);
                    }
                }

                break;

            case SeriesDeleteKind.EpisodeKeepLast:
                foreach (var item in items)
                {
                    var episode = (Episode)item.Item;
                    var seriesStatus = episode.Series.Status;
                    var lastEpisodeId = episode.Series.GetEpisodes(item.Data?.First()?.User, new DtoOptions()).LastOrDefault()?.Id;
                    if (seriesStatus.HasValue
                        && seriesStatus.Value != SeriesStatus.Ended
                        && episode.Id == lastEpisodeId)
                    {
                        _logger.LogTrace("Episode \"{EpisodeName}\" was NOT added to expired items because this is the last one and metadata indicates that show is not yet finished",
                            item.FullName);
                        continue;
                    }

                    result.Add(item);
                }
                break;

            default:
                result = items;
                break;
        }

        return result;
    }
}
