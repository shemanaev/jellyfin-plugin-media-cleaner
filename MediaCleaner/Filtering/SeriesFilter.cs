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

    public List<ExpiredItem> Apply(List<ExpiredItem> items)
    {
        var result = new List<ExpiredItem>();

        _logger.LogInformation("SeriesFilter start: {Items}", items.Select(x => x.Item.Id));

        switch (_kind)
        {
            case SeriesDeleteKind.Season:
                var seasons = items.GroupBy(x => x.Item is Episode episode ? episode.Season?.Id ?? episode.Series?.Id : null);
                _logger.LogInformation("SeriesFilter seasons: {Count}, {Items}", seasons.Count(), seasons.Select(x => x.Key));
                foreach (var season in seasons)
                {
                    _logger.LogInformation("SeriesFilter season: {Id}", season.Key);
                    var first = season.MaxBy(x => x.Data?.First()?.LastPlayedDate ?? x.Item.DateCreated);
                    if (first?.Item is not Episode episode) continue;
                    if (episode.Season == null) continue;
                    _logger.LogInformation("SeriesFilter first episode: {episode}", episode.Id);
                    var episodes = episode.Season.GetEpisodes().Where(x => !x.IsVirtualItem).ToList();
                    var allWatched = season.Count() == episodes.Count && season.All(value => episodes.Contains(value.Item));

                    _logger.LogInformation("SeriesFilter allWatched: {allWatched}", allWatched);

                    _logger.LogDebug("\"{Username}\" has watched episodes {Count} of {Total} in season \"{SeriesName}\": \"{SeasonName}\"",
                        season.First().Data?.First()?.User.Username ?? "[None]", season.Count(), episodes.Count, episode.Series.Name, episode.Season.Name);

                    if (allWatched)
                    {
                        _logger.LogInformation("SeriesFilter allWatched true: {OriginalTitle}", episode.Season);

                        result.Add(new ExpiredItem
                        {
                            Item = episode.Season,
                            Data = first.Data,
                            Kind = first.Kind,
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
                    var first = show.MaxBy(x => x.Data?.First()?.LastPlayedDate ?? x.Item.DateCreated);
                    if (first?.Item is not Episode episode) continue;
                    if (episode.Series == null) continue;
                    var episodes = episode.Series.GetEpisodes().Where(x => !x.IsVirtualItem).ToList();
                    var allWatched = show.Count() == episodes.Count && show.All(value => episodes.Contains(value.Item));

                    _logger.LogDebug("\"{Username}\" has watched episodes {Count} of {Total} in series \"{SeriesName}\"'",
                        show.First().Data?.First()?.User.Username ?? "[None]", show.Count(), episodes.Count, episode.Series.Name);

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
                            Data = first.Data,
                            Kind = first.Kind,
                        });

                        _logger.LogTrace("Series \"{SeriesName}\" was added to expired items", episode.SeriesName);
                    }
                }

                break;

            default:
                result = items;
                break;
        }

        return result;
    }
}
