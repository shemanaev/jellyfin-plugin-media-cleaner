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

    public enum SeriesDeleteKind
    {
        Episode,
        Season,
        Series,
        SeriesEnded
    }

    public enum LocationsListMode
    {
        Exclude,
        Include
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        public int KeepMoviesFor { get; set; } = -1;
        public FavoriteKeepKind KeepFavoriteMovies { get; set; } = FavoriteKeepKind.AnyUser;

        public int KeepEpisodesFor { get; set; } = -1;
        public FavoriteKeepKind KeepFavoriteEpisodes { get; set; } = FavoriteKeepKind.AnyUser;
        public SeriesDeleteKind DeleteEpisodes { get; set; } = SeriesDeleteKind.Season;

        public int KeepVideosFor { get; set; } = -1;
        public FavoriteKeepKind KeepFavoriteVideos { get; set; } = FavoriteKeepKind.AnyUser;

        public List<string> UsersIgnorePlayed { get; set; } = new List<string>();
        public List<string> UsersIgnoreFavorited { get; set; } = new List<string>();

        // This should be named just "Locations", but it was list of exclusion historically
        // and we don't want to break anyone's config.
        public List<string> LocationsExcluded { get; set; } = new List<string>();
        public LocationsListMode LocationsMode { get; set; } = LocationsListMode.Exclude;

        public bool MarkAsUnplayed { get; set; } = false;
    }
}
