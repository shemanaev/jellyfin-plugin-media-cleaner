using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace MediaCleaner.Configuration
{
    public enum FavoriteKeepKind
    {
        DontKeep,
        AnyUser,
        AllUsers
    }

    public enum PlayedKeepKind
    {
        AnyUser,
        AnyUserRolling,
        AllUsers
    }

    public enum SeriesDeleteKind
    {
        Episode,
        Season,
        Series,
        SeriesEnded,
        EpisodeKeepLast,
        SeasonKeepLast,
    }

    public enum LocationsListMode
    {
        Exclude,
        Include
    }

    public enum UsersListMode
    {
        Ignore,
        Acknowledge
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        public int KeepMoviesFor { get; set; } = -1;
        public int KeepMoviesNotPlayedFor { get; set; } = -1;
        public PlayedKeepKind KeepPlayedMovies { get; set; } = PlayedKeepKind.AnyUser;
        public FavoriteKeepKind KeepFavoriteMovies { get; set; } = FavoriteKeepKind.AnyUser;

        public int KeepEpisodesFor { get; set; } = -1;
        public int KeepEpisodesNotPlayedFor { get; set; } = -1;
        public PlayedKeepKind KeepPlayedEpisodes { get; set; } = PlayedKeepKind.AnyUser;
        public FavoriteKeepKind KeepFavoriteEpisodes { get; set; } = FavoriteKeepKind.AnyUser;
        public SeriesDeleteKind DeleteEpisodes { get; set; } = SeriesDeleteKind.Season;

        public int KeepVideosFor { get; set; } = -1;
        public int KeepVideosNotPlayedFor { get; set; } = -1;
        public PlayedKeepKind KeepPlayedVideos { get; set; } = PlayedKeepKind.AnyUser;
        public FavoriteKeepKind KeepFavoriteVideos { get; set; } = FavoriteKeepKind.AnyUser;

        public int KeepAudioFor { get; set; } = -1;
        public int KeepAudioNotPlayedFor { get; set; } = -1;
        public PlayedKeepKind KeepPlayedAudio { get; set; } = PlayedKeepKind.AnyUser;
        public FavoriteKeepKind KeepFavoriteAudio { get; set; } = FavoriteKeepKind.AnyUser;

        public int KeepAudioBooksFor { get; set; } = -1;
        public int KeepAudioBooksNotPlayedFor { get; set; } = -1;
        public PlayedKeepKind KeepPlayedAudioBooks { get; set; } = PlayedKeepKind.AnyUser;
        public FavoriteKeepKind KeepFavoriteAudioBooks { get; set; } = FavoriteKeepKind.AnyUser;

        public List<string> UsersIgnorePlayed { get; set; } = new List<string>();
        public UsersListMode UsersPlayedMode { get; set; } = UsersListMode.Ignore;

        public List<string> UsersIgnoreFavorited { get; set; } = new List<string>();
        public UsersListMode UsersFavoritedMode { get; set; } = UsersListMode.Ignore;

        // This should be named just "Locations", but it was list of exclusion historically
        // and we don't want to break anyone's config.
        public List<string> LocationsExcluded { get; set; } = new List<string>();
        public LocationsListMode LocationsMode { get; set; } = LocationsListMode.Exclude;

        public bool MarkAsUnplayed { get; set; } = false;
    }
}
