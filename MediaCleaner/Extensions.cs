using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace MediaCleaner
{
    internal static class Extensions
    {
        public static IEnumerable<BaseItem> GetEpisodes(this Series series)
        {
            return series.GetSeasons(null, new DtoOptions(true)).SelectMany(x => ((Season)x).GetEpisodes());
        }
    }
}
