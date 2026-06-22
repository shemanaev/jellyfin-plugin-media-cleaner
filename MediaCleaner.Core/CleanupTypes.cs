using System;
using System.Collections.Generic;

namespace MediaCleaner.Core;

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

public enum MediaItemKind
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

public enum MediaSeriesStatus
{
    Unknown,
    Continuing,
    Ended,
}

public enum ExpiredKind
{
    Played,
    NotPlayed,
    AddedAge,
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

public enum CleanupAuditStage
{
    RuleEligibility,
    Trigger,
    FavoriteFilter,
    LocationFilter,
    TagFilter,
    SeriesPolicy,
    Protection,
    DeletionCascade,
}

public enum CleanupAuditOutcome
{
    Matched,
    Rejected,
    Protected,
    Suppressed,
    Planned,
    Blocked,
    Skipped,
}

public enum RuleFavoriteFilterKind
{
    Ignore,
    FavoriteByAnyUser,
    FavoriteByAllUsers,
    NotFavoriteByAnyUser,
    NotFavoriteByAllUsers,
}

public sealed record CleanupRuleTrigger(
    CleanupRuleTriggerKind Kind,
    int Days,
    PlayedKeepKind PlayedKeepKind = PlayedKeepKind.AnyUser,
    int CountAsNotPlayedAfter = -1);

public sealed record CleanupRuleFilters(
    IReadOnlyList<MediaItemKind> MediaKinds,
    IReadOnlyList<string> UserIds,
    UsersListMode UsersMode,
    IReadOnlyList<string> FavoriteUserIds,
    UsersListMode FavoriteUsersMode,
    RuleFavoriteFilterKind FavoriteFilter,
    IReadOnlyList<string> Locations,
    LocationsListMode LocationsMode,
    bool EnableTagFilter,
    TagMode TagFilterMode,
    IReadOnlyList<string> Tags,
    SeriesDeleteKind DeleteEpisodes,
    SeriesKeepKind KeepSeriesKind);

public sealed record CleanupRuleActions(
    CleanupRuleActionKind Kind,
    bool MarkAsUnplayed);

public sealed record CleanupRule(
    string Id,
    string Name,
    bool Enabled,
    CleanupRuleTrigger Trigger,
    CleanupRuleFilters Filters,
    CleanupRuleActions Actions);

public sealed record CleanupPolicy(
    IReadOnlyList<CleanupRule> Rules,
    bool AllowDeleteIfPlayedBeforeAdded);

public sealed record MediaUser(string Id, string Username);

public sealed record PlaybackState(
    string UserId,
    DateTime? LastPlayedDate,
    bool IsPlayed,
    bool IsWatching,
    bool IsFavorite,
    string? UserName = null,
    bool HasUserData = true);

public sealed record MediaItem(
    string Id,
    MediaItemKind Kind,
    string Name,
    string FullName,
    DateTime DateCreated,
    string? Path,
    string? LocationPath,
    IReadOnlyList<string> Tags,
    IReadOnlyList<PlaybackState> Playback,
    string? SeriesId = null,
    string? SeasonId = null,
    string? SeriesName = null,
    string? SeasonName = null,
    int? ParentIndexNumber = null,
    int? IndexNumber = null,
    bool IsVirtual = false,
    MediaSeriesStatus SeriesStatus = MediaSeriesStatus.Unknown,
    IReadOnlyList<string>? EpisodeIds = null,
    IReadOnlyList<string>? SeasonEpisodeIds = null,
    IReadOnlyList<string>? SeriesEpisodeIds = null,
    IReadOnlyList<string>? SeasonIds = null,
    string? FirstEpisodeId = null,
    string? LastEpisodeId = null,
    string? FirstSeasonId = null,
    string? LastSeasonId = null);

public sealed record CleanupRequest(
    CleanupPolicy Policy,
    IReadOnlyList<MediaUser> Users,
    IReadOnlyList<MediaItem> Items,
    bool IsDryRun);

public sealed record CleanupDecision(
    MediaItem Item,
    ExpiredKind Kind,
    IReadOnlyList<PlaybackState> Playback,
    string Reason,
    ActivityNotification Notification,
    IReadOnlyList<string> MarkUnplayedUserIds,
    IReadOnlyList<string> MatchedRules);

public sealed record CleanupAuditEntry(
    string? ItemId,
    string? ItemName,
    MediaItemKind? ItemKind,
    string? RuleId,
    string? RuleName,
    CleanupRuleActionKind? Action,
    CleanupAuditStage Stage,
    CleanupAuditOutcome Outcome,
    string Reason);

public sealed record DeletionOperation(string ItemId, MediaItemKind Kind, string Name);

public sealed record ActivityNotification(string Title, string ShortOverview, string Overview);

public sealed record CleanupPlan(
    IReadOnlyList<CleanupDecision> Decisions,
    IReadOnlyList<DeletionOperation> Deletions,
    IReadOnlyList<CleanupAuditEntry> AuditEntries)
{
    public static CleanupPlan Empty { get; } = new([], [], []);
}

public sealed record TagReplacementItem(string Id, IReadOnlyList<string>? Tags);

public sealed record TagReplacementUpdate(string ItemId, IReadOnlyList<string> Tags);

public sealed record TagReplacementPlan(
    string OldTag,
    string NewTag,
    IReadOnlyList<TagReplacementUpdate> Updates,
    int UpdatedCount,
    int SkippedCount,
    int ErrorCount,
    int TotalProcessed);
