using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace MediaCleaner.Models;

internal class ExpiredItem
{
    public BaseItem Item { get; init; } = default!;
    public List<ExpiredItemData> Data { get; init; } = default!;
    public ExpiredReason Reason { get; init; }

    public string FullName
    {
        get
        {
            return Item switch
            {
                Movie movie => movie.Name,
                Series series => series.Name,
                Season season => $"{season.SeriesName} | S{season.IndexNumber:D2} | {season.Name}",
                Episode episode => $"{episode.SeriesName} | S{episode.ParentIndexNumber:D2}E{episode.IndexNumber:D2} | {episode.SeasonName} | {episode.Name}",
                Video video => video.Name,
                _ => Item.Name,
            };
        }
    }
}
