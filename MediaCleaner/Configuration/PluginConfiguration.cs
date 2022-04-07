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
        Series
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        public int KeepMoviesFor { get; set; } = -1;
        public FavoriteKeepKind KeepFavoriteMovies { get; set; } = FavoriteKeepKind.AnyUser;

        public int KeepEpisodesFor { get; set; } = -1;
        public FavoriteKeepKind KeepFavoriteEpisodes { get; set; } = FavoriteKeepKind.AnyUser;
        public SeriesDeleteKind DeleteEpisodes { get; set; } = SeriesDeleteKind.Season;

        public List<string> UsersIgnorePlayed { get; set; } = new List<string>();
        public List<string> UsersIgnoreFavorited { get; set; } = new List<string>();

        public List<string> LocationsExcluded { get; set; } = new List<string>();
    }
}
