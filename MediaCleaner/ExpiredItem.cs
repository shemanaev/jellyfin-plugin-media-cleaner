using System;
using Jellyfin.Data.Entities;
using MediaBrowser.Controller.Entities;

namespace MediaCleaner
{
    internal class ExpiredItem
    {
        public BaseItem Item { get; set; } = default!;
        public User User { get; set; } = default!;
        public DateTime LastPlayedDate { get; set; }
    }
}
