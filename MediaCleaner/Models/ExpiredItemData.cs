using System;
using Jellyfin.Database.Implementations.Entities;

namespace MediaCleaner.Models;

internal class ExpiredItemData
{
    public User User { get; init; } = default!;
    public DateTime LastPlayedDate { get; init; }
    public bool IsPlayed { get; init; }
    public bool IsWatching { get; init; }
}
