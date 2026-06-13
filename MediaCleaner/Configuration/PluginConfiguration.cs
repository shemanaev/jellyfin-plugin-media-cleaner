using System.Collections.Generic;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Plugins;
using MediaCleaner.Models;

namespace MediaCleaner.Configuration;

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

public enum TroubleshootingLogDateFormat
{
    YYYYMMDD,
    DDMMYYYY,
    MMDDYYYY
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

    public List<string> UsersIgnorePlayed { get; set; } = [];
    public UsersListMode UsersPlayedMode { get; set; } = UsersListMode.Ignore;

    public List<string> UsersIgnoreFavorited { get; set; } = [];
    public UsersListMode UsersFavoritedMode { get; set; } = UsersListMode.Ignore;

    // This should be named just "Locations", but it was list of exclusion historically
    // and we don't want to break anyone's config.
    public List<string> LocationsExcluded { get; set; } = [];
    public LocationsListMode LocationsMode { get; set; } = LocationsListMode.Exclude;

    public bool MarkAsUnplayed { get; set; } = false;
    public int CountAsNotPlayedAfter { get; set; } = -1;
    public bool AllowDeleteIfPlayedBeforeAdded { get; set; } = false;

    public bool EnableTagExclusion { get; set; } = true;
    public TagMode TagFilterMode { get; set; } = TagMode.Exclusion;
    public string ExclusionTag { get; set; } = "mediacleaner_keep";
    public string InclusionTag { get; set; } = "mediacleaner_delete";
    public bool ReplaceExclusionTag { get; set; } = false;

    public int LeavingSoonDays { get; set; } = -1;

    public bool RadarrEnabled { get; set; } = false;
    public string RadarrBaseUrl { get; set; } = string.Empty;
    public string RadarrApiKey { get; set; } = string.Empty;
    public int RadarrTimeoutSeconds { get; set; } = 30;

    public bool SonarrEnabled { get; set; } = false;
    public string SonarrBaseUrl { get; set; } = string.Empty;
    public string SonarrApiKey { get; set; } = string.Empty;
    public int SonarrTimeoutSeconds { get; set; } = 30;

    public TroubleshootingLogDateFormat TroubleshootingLogDateFormat { get; set; } = TroubleshootingLogDateFormat.YYYYMMDD;
}
