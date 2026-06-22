using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MediaCleaner.Core;

internal static class CleanupDecisionFactory
{
    public static CleanupDecision Create(
        MediaItem item,
        ExpiredKind kind,
        IReadOnlyList<PlaybackState> playback,
        IReadOnlyList<string> markUnplayedUsers,
        IReadOnlyList<string> matchedRules)
    {
        var notification = CreateBaseNotification(item, kind, playback);
        var reason = kind switch
        {
            ExpiredKind.Played => $"expired for {string.Join(", ", playback.Select(DisplayUser))}; matched {string.Join(", ", matchedRules)}",
            ExpiredKind.NotPlayed => $"not played since {item.DateCreated.ToLocalTime().ToString(CultureInfo.CurrentCulture)}; matched {string.Join(", ", matchedRules)}",
            ExpiredKind.AddedAge => $"added at {item.DateCreated.ToLocalTime().ToString(CultureInfo.CurrentCulture)}; matched {string.Join(", ", matchedRules)}",
            _ => throw new NotSupportedException($"Unsupported expired kind: {kind}"),
        };

        return new CleanupDecision(item, kind, playback, reason, notification, markUnplayedUsers, matchedRules);
    }

    private static string DisplayUser(PlaybackState playback) => playback.UserName ?? playback.UserId;

    private static ActivityNotification CreateBaseNotification(MediaItem item, ExpiredKind kind, IReadOnlyList<PlaybackState> playback)
    {
        var title = item.Kind switch
        {
            MediaItemKind.Season => $"\"{item.SeriesName}\" S{item.IndexNumber:D2} was deleted",
            MediaItemKind.Episode => $"\"{item.SeriesName}\" S{item.ParentIndexNumber:D2}E{item.IndexNumber:D2} was deleted",
            _ => $"\"{item.Name}\" was deleted",
        };
        var shortOverview = kind switch
        {
            ExpiredKind.Played => playback.Count == 0
                ? "Played item expired"
                : $"Last played by {DisplayUser(playback.First())} at {playback.First().LastPlayedDate!.Value.ToLocalTime()}",
            ExpiredKind.NotPlayed => $"Not played by anyone since {item.DateCreated.ToLocalTime()}",
            ExpiredKind.AddedAge => $"Added at {item.DateCreated.ToLocalTime()}",
            _ => throw new NotSupportedException($"Unsupported expired kind: {kind}"),
        };
        return new ActivityNotification(title, shortOverview, item.Path ?? string.Empty);
    }
}
