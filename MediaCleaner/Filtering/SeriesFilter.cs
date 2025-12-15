using System.Collections.Generic;
using System.Linq;
using MediaCleaner.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.Filtering;

internal class SeriesFilter(ILogger<SeriesFilter> logger, SeriesDeleteKind kind, SeriesKeepKind keepKind) : IExpiredItemFilter
{
    public string Name => "Series";

    public List<ExpiredItem> Apply(List<ExpiredItem> items)
    {
        var result = new List<ExpiredItem>();

        switch (kind)
        {
            case SeriesDeleteKind.Season:
                var seasons = items.GroupBy(x => ((Episode)x.Item).Season?.Id ?? ((Episode)x.Item).Series?.Id);
                foreach (var season in seasons)
                {
                    var first = season.MaxBy(x => x.Data?.First()?.LastPlayedDate ?? x.Item.DateCreated);
                    if (first?.Item is not Episode episode) continue;
                    if (episode.Season is null)
                    {
                        logger.LogDebug("Skipping episode \"{EpisodeName}\" because it has no season", episode.Name);
                        continue;
                    }
                    var episodes = episode.Season.GetEpisodes().Where(x => !x.IsVirtualItem).ToList();
                    var allWatched = season.Count() == episodes.Count && season.All(value => episodes.Contains(value.Item));

                    logger.LogDebug("\"{Username}\" has watched episodes {Count} of {Total} in season \"{SeriesName}\": \"{SeasonName}\"",
                        season.First().Data?.First()?.User.Username ?? "[None]", season.Count(), episodes.Count, episode.Series?.Name ?? "[Unknown]", episode.Season.Name);

                    if (allWatched)
                    {
                        var expiredItem = new ExpiredItem
                        {
                            Item = episode.Season,
                            Data = first.Data,
                            Kind = first.Kind,
                        };

                        if (keepKind == SeriesKeepKind.First)
                        {
                            var firstSeasonId = episode.Series?.GetSeasons(first.Data?.First()?.User, new DtoOptions()).FirstOrDefault()?.Id;
                            if (episode.Season.Id == firstSeasonId)
                            {
                                logger.LogTrace("Season \"{SeasonName}\" was NOT added to expired items because this is the first one",
                                    expiredItem.FullName);
                                continue;
                            }
                        }

                        if (keepKind == SeriesKeepKind.Last)
                        {
                            var lastSeasonId = episode.Series?.GetSeasons(first.Data?.First()?.User, new DtoOptions()).LastOrDefault()?.Id;
                            if (episode.Season.Id == lastSeasonId)
                            {
                                logger.LogTrace("Season \"{SeasonName}\" was NOT added to expired items because this is the last one",
                                    expiredItem.FullName);
                                continue;
                            }
                        }

                        result.Add(expiredItem);

                        logger.LogTrace("Season \"{SeasonName}\" was added to expired items", expiredItem.FullName);
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
                    if (episode.Series is null)
                    {
                        logger.LogDebug("Skipping episode \"{EpisodeName}\" because it has no series", episode.Name);
                        continue;
                    }
                    var episodes = episode.Series.GetEpisodes().Where(x => !x.IsVirtualItem).ToList();
                    var allWatched = show.Count() == episodes.Count && show.All(value => episodes.Contains(value.Item));

                    logger.LogDebug("\"{Username}\" has watched episodes {Count} of {Total} in series \"{SeriesName}\"'",
                        show.First().Data?.First()?.User.Username ?? "[None]", show.Count(), episodes.Count, episode.Series.Name);

                    if (allWatched)
                    {
                        var expiredItem = new ExpiredItem
                        {
                            Item = episode.Series,
                            Data = first.Data,
                            Kind = first.Kind,
                        };

                        if (kind == SeriesDeleteKind.SeriesEnded)
                        {
                            var seriesStatus = episode.Series.Status;
                            if (seriesStatus.HasValue
                                && seriesStatus.Value != SeriesStatus.Ended)
                            {
                                logger.LogTrace("Series \"{SeriesName}\" was NOT added to expired items because metadata indicates that it's not yet finished",
                                    expiredItem.FullName);
                                continue;
                            }
                        }

                        result.Add(expiredItem);

                        logger.LogTrace("Series \"{SeriesName}\" was added to expired items", episode.SeriesName);
                    }
                }

                break;

            case SeriesDeleteKind.Episode:
                foreach (var item in items)
                {
                    var episode = (Episode)item.Item;

                    if (keepKind == SeriesKeepKind.First)
                    {
                        var firstEpisodeId = episode.Series.GetEpisodes(item.Data?.First()?.User, new DtoOptions(), false).FirstOrDefault()?.Id;
                        if (episode.Id == firstEpisodeId)
                        {
                            logger.LogTrace("Episode \"{EpisodeName}\" was NOT added to expired items because this is the first one",
                                item.FullName);
                            continue;
                        }
                    }

                    if (keepKind == SeriesKeepKind.Last)
                    {
                        var lastEpisodeId = episode.Series.GetEpisodes(item.Data?.First()?.User, new DtoOptions(), false).LastOrDefault()?.Id;
                        if (episode.Id == lastEpisodeId)
                        {
                            logger.LogTrace("Episode \"{EpisodeName}\" was NOT added to expired items because this is the last one",
                                item.FullName);
                            continue;
                        }
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
