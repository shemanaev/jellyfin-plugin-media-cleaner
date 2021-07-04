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
    }
}
