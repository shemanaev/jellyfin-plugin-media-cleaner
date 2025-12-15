using System.Collections.Generic;
using Jellyfin.Data.Enums;
using MediaCleaner.Models;

namespace MediaCleaner.Configuration;

internal record ConfigMediaNode(BaseItemKind ItemKind, ExpiredReason Reason, int KeepDays, PlayedKeepKind UserMode, FavoriteKeepKind FavoriteMode, object? SpecificOptions = null);

internal record ConfigSeriesSpecificNode(SeriesDeleteKind GroupMode, SeriesKeepKind KeepMode);

internal record ConfigUserListNode(List<string> Users, UsersListMode Mode);

internal record ConfigLocationListNode(List<string> Locations, LocationsListMode Mode);

internal record ConfigTagsNode(bool Enabled, TagMode Mode, string ExcludeTag, string IncludeTag);

internal record ConfigLeavingSoonNode(int Days);

internal record ConfigMiscNode(bool MarkAsUnplayed, int CountAsNotPlayedAfter, bool AllowDeleteIfPlayedBeforeAdded);

internal record StructuredConfig(
    List<ConfigMediaNode> MediaNodes,
    ConfigUserListNode UsersIgnore,
    ConfigUserListNode UsersFavorites,
    ConfigLocationListNode Locations,
    ConfigTagsNode Tags,
    ConfigLeavingSoonNode LeavingSoon,
    ConfigMiscNode Misc)
{
    public static StructuredConfig CreateFromConfiguration(PluginConfiguration config)
    {
        return new(
            [
                new(BaseItemKind.Movie, ExpiredReason.Played, config.KeepMoviesFor, config.KeepPlayedMovies, config.KeepFavoriteMovies),
                new(BaseItemKind.Movie, ExpiredReason.NotPlayed, config.KeepMoviesNotPlayedFor, config.KeepPlayedMovies, config.KeepFavoriteMovies),
                new(BaseItemKind.Episode, ExpiredReason.Played, config.KeepEpisodesFor, config.KeepPlayedEpisodes, config.KeepFavoriteEpisodes, new ConfigSeriesSpecificNode(config.DeleteEpisodes, config.KeepSeriesKind)),
                new(BaseItemKind.Episode, ExpiredReason.NotPlayed, config.KeepEpisodesNotPlayedFor, config.KeepPlayedEpisodes, config.KeepFavoriteEpisodes, new ConfigSeriesSpecificNode(config.DeleteEpisodes, config.KeepSeriesKind)),
                new(BaseItemKind.Video, ExpiredReason.Played, config.KeepVideosFor, config.KeepPlayedVideos, config.KeepFavoriteVideos),
                new(BaseItemKind.Video, ExpiredReason.NotPlayed, config.KeepVideosNotPlayedFor, config.KeepPlayedVideos, config.KeepFavoriteVideos),
                new(BaseItemKind.Audio, ExpiredReason.Played, config.KeepAudioFor, config.KeepPlayedAudio, config.KeepFavoriteAudio),
                new(BaseItemKind.Audio, ExpiredReason.NotPlayed, config.KeepAudioNotPlayedFor, config.KeepPlayedAudio, config.KeepFavoriteAudio),
                new(BaseItemKind.AudioBook, ExpiredReason.Played, config.KeepAudioBooksFor, config.KeepPlayedAudioBooks, config.KeepFavoriteAudioBooks),
                new(BaseItemKind.AudioBook, ExpiredReason.NotPlayed, config.KeepAudioBooksNotPlayedFor, config.KeepPlayedAudioBooks, config.KeepFavoriteAudioBooks)
            ],
            new ConfigUserListNode(config.UsersIgnorePlayed, config.UsersPlayedMode),
            new ConfigUserListNode(config.UsersIgnoreFavorited, config.UsersFavoritedMode),
            new ConfigLocationListNode(config.LocationsExcluded, config.LocationsMode),
            new ConfigTagsNode(config.EnableTagExclusion, config.TagFilterMode, config.ExclusionTag, config.InclusionTag),
            new ConfigLeavingSoonNode(config.LeavingSoonDays),
            new ConfigMiscNode(config.MarkAsUnplayed, config.CountAsNotPlayedAfter, config.AllowDeleteIfPlayedBeforeAdded)
        );
    }
}
