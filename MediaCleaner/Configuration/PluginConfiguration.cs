using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace MediaCleaner.Configuration
{
    public enum TagMode
    {
        Exclusion,
        Inclusion
    }

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
    }

    public enum SeriesKeepKind
    {
        None,
        First,
        Last,
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

    public enum RuleMediaKind
    {
        Movie,
        Series,
        Season,
        Episode,
        Video,
        Audio,
        AudioBook,
        Other,
    }

    public enum CleanupRuleTriggerKind
    {
        Played,
        NotPlayed,
        AddedAge,
    }

    public enum CleanupRuleActionKind
    {
        Delete,
        Protect,
    }

    public enum RuleFavoriteFilterKind
    {
        Ignore,
        FavoriteByAnyUser,
        FavoriteByAllUsers,
        NotFavoriteByAnyUser,
        NotFavoriteByAllUsers,
    }

    public class CleanupRuleTriggerConfiguration
    {
        public CleanupRuleTriggerKind Kind { get; set; } = CleanupRuleTriggerKind.Played;
        public int Days { get; set; } = -1;
        public PlayedKeepKind PlayedKeepKind { get; set; } = PlayedKeepKind.AnyUser;
        public int CountAsNotPlayedAfter { get; set; } = -1;
    }

    public class CleanupRuleFiltersConfiguration
    {
        public List<RuleMediaKind> MediaKinds { get; set; } = new List<RuleMediaKind>();
        public List<string> UserIds { get; set; } = new List<string>();
        public UsersListMode UsersMode { get; set; } = UsersListMode.Ignore;
        public List<string> FavoriteUserIds { get; set; } = new List<string>();
        public UsersListMode FavoriteUsersMode { get; set; } = UsersListMode.Ignore;
        public RuleFavoriteFilterKind FavoriteFilter { get; set; } = RuleFavoriteFilterKind.Ignore;
        public List<string> Locations { get; set; } = new List<string>();
        public LocationsListMode LocationsMode { get; set; } = LocationsListMode.Exclude;
        public bool EnableTagFilter { get; set; } = false;
        public TagMode TagFilterMode { get; set; } = TagMode.Exclusion;
        public List<string> Tags { get; set; } = new List<string>();
        public SeriesDeleteKind DeleteEpisodes { get; set; } = SeriesDeleteKind.Season;
        public SeriesKeepKind KeepSeriesKind { get; set; } = SeriesKeepKind.None;
    }

    public class CleanupRuleActionsConfiguration
    {
        public CleanupRuleActionKind Kind { get; set; } = CleanupRuleActionKind.Delete;
        public bool MarkAsUnplayed { get; set; } = false;
    }

    public class CleanupRuleConfiguration
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public CleanupRuleTriggerConfiguration Trigger { get; set; } = new CleanupRuleTriggerConfiguration();
        public CleanupRuleFiltersConfiguration Filters { get; set; } = new CleanupRuleFiltersConfiguration();
        public CleanupRuleActionsConfiguration Actions { get; set; } = new CleanupRuleActionsConfiguration();
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        public int ConfigVersion { get; set; } = 1;
        public List<CleanupRuleConfiguration> Rules { get; set; } = new List<CleanupRuleConfiguration>();

        public int KeepMoviesFor { get; set; } = -1;
        public int KeepMoviesNotPlayedFor { get; set; } = -1;
        public PlayedKeepKind KeepPlayedMovies { get; set; } = PlayedKeepKind.AnyUser;
        public FavoriteKeepKind KeepFavoriteMovies { get; set; } = FavoriteKeepKind.AnyUser;

        public int KeepEpisodesFor { get; set; } = -1;
        public int KeepEpisodesNotPlayedFor { get; set; } = -1;
        public PlayedKeepKind KeepPlayedEpisodes { get; set; } = PlayedKeepKind.AnyUser;
        public FavoriteKeepKind KeepFavoriteEpisodes { get; set; } = FavoriteKeepKind.AnyUser;
        public SeriesDeleteKind DeleteEpisodes { get; set; } = SeriesDeleteKind.Season;
        public SeriesKeepKind KeepSeriesKind { get; set; } = SeriesKeepKind.None;

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
        public int CountAsNotPlayedAfter { get; set; } = -1;
        public bool AllowDeleteIfPlayedBeforeAdded { get; set; } = false;

        public bool EnableTagExclusion { get; set; } = true;
        public TagMode TagFilterMode { get; set; } = TagMode.Exclusion;
        public string ExclusionTag { get; set; } = "mediacleaner_keep";
        public string InclusionTag { get; set; } = "mediacleaner_delete";
        public bool ReplaceExclusionTag { get; set; } = false;
    }
}
