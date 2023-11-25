using System;
using System.Collections.Generic;
using Jellyfin.Data.Entities;
using MediaBrowser.Controller.Entities;

namespace MediaCleaner
{
    internal class ExpiredItem
    {
        public BaseItem Item { get; set; } = default!;
        public List<ExpiredItemData> Data { get; set; } = default!;
    }

    internal class ExpiredItemData
    {
        public User User { get; set; } = default!;
        public DateTime LastPlayedDate { get; set; }
        public bool IsPlayed { get; set; }
        public bool IsWatching { get; set; }
    }
}
