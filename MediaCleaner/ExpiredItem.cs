using System;
using Jellyfin.Data.Entities;
using MediaBrowser.Controller.Entities;

namespace MediaCleaner
{
    internal class ExpiredItem
    {
        public BaseItem Item { get; set; }
        public User User { get; set; }
        public DateTime LastPlayedDate { get; set; }
    }
}
