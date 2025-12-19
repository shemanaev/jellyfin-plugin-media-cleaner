using System;
using Jellyfin.Data.Enums;
using MediaCleaner.Models;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.JunkCollectors;

internal static class JunkCollectorFactory
{
    public static IJunkCollector CreateInstance(ILoggerFactory loggerFactory, ItemsAdapter itemsAdapter, BaseItemKind kind, ExpiredReason expiredReason) =>
        (kind, expiredReason) switch
        {
            (BaseItemKind.AudioBook, ExpiredReason.Played) => new AudioBookPlayedJunkCollector(loggerFactory.CreateLogger<AudioBookPlayedJunkCollector>(), itemsAdapter),
            (BaseItemKind.AudioBook, ExpiredReason.NotPlayed) => new AudioBookNotPlayedJunkCollector(loggerFactory.CreateLogger<AudioBookNotPlayedJunkCollector>(), itemsAdapter),
            (BaseItemKind.Audio, ExpiredReason.Played) => new AudioPlayedJunkCollector(loggerFactory.CreateLogger<AudioPlayedJunkCollector>(), itemsAdapter),
            (BaseItemKind.Audio, ExpiredReason.NotPlayed) => new AudioNotPlayedJunkCollector(loggerFactory.CreateLogger<AudioNotPlayedJunkCollector>(), itemsAdapter),
            (BaseItemKind.Episode, ExpiredReason.Played) => new SeriesPlayedJunkCollector(loggerFactory.CreateLogger<SeriesPlayedJunkCollector>(), itemsAdapter),
            (BaseItemKind.Episode, ExpiredReason.NotPlayed) => new SeriesNotPlayedJunkCollector(loggerFactory.CreateLogger<SeriesNotPlayedJunkCollector>(), itemsAdapter),
            (BaseItemKind.Movie, ExpiredReason.Played) => new MoviesPlayedJunkCollector(loggerFactory.CreateLogger<MoviesPlayedJunkCollector>(), itemsAdapter),
            (BaseItemKind.Movie, ExpiredReason.NotPlayed) => new MoviesNotPlayedJunkCollector(loggerFactory.CreateLogger<MoviesNotPlayedJunkCollector>(), itemsAdapter),
            (BaseItemKind.Video, ExpiredReason.Played) => new VideosPlayedJunkCollector(loggerFactory.CreateLogger<VideosPlayedJunkCollector>(), itemsAdapter),
            (BaseItemKind.Video, ExpiredReason.NotPlayed) => new VideosNotPlayedJunkCollector(loggerFactory.CreateLogger<VideosNotPlayedJunkCollector>(), itemsAdapter),
            _ => throw new ArgumentException("Unknown junk type"),
        };
}
