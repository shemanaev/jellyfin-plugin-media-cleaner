using System;
using System.Collections.Generic;
using Jellyfin.Data.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace MediaCleaner
{
    internal class ExpiredItem
    {
        public BaseItem Item { get; set; } = default!;
        public List<ExpiredItemData> Data { get; set; } = default!;
        public ExpiredKind Kind { get; set; }

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

    internal class ExpiredItemData
    {
        public User User { get; set; } = default!;
        public DateTime LastPlayedDate { get; set; }
        public bool IsPlayed { get; set; }
        public bool IsWatching { get; set; }
    }
}
