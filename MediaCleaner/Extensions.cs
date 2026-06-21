using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaCleaner.Compatibility;

namespace MediaCleaner
{
    internal static class Extensions
    {
        public static IEnumerable<BaseItem> GetEpisodes(this Series series)
        {
            return JellyfinCompatibility.GetEpisodes(series);
        }
    }
}
