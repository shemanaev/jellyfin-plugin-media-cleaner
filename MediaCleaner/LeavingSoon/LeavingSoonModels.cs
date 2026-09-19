using System;
using System.Collections.Generic;
using MediaCleaner.Core;

namespace MediaCleaner.LeavingSoon;

public enum WarningStatus
{
    PendingPublication,
    Warning,
    Ready,
    Deleting,
    Deleted,
}

public enum ProtectionPlaybackStatus
{
    NotPlayed,
    Played,
    UnknownUser,
    MissingItem,
    UnavailableUserData,
}

public sealed record ProtectionPlaybackObservation(ProtectionPlaybackStatus Status, DateTime? LastPlayedDateUtc = null, string? UserName = null);

public sealed record LeavingSoonCandidate(
    string ItemId,
    MediaItemKind Kind,
    string Name,
    string? Path,
    string IdentityHash,
    string ConfigurationFingerprint,
    IReadOnlyList<string> RuleIds,
    int NoticeDays,
    string NoticeGeneration = "initial");

public sealed record WarningEntry(
    string ItemId,
    MediaItemKind Kind,
    string Name,
    string? Path,
    string IdentityHash,
    string ConfigurationFingerprint,
    WarningStatus Status,
    DateTime FirstDetectedUtc,
    DateTime? FirstPublishedUtc,
    DateTime? NotBeforeDeleteUtc,
    DateTime LastSeenUtc,
    int NoticeDays,
    IReadOnlyList<string> RuleIds,
    string? LastError,
    string NoticeGeneration = "initial");

public sealed record ViewProtection(
    string ProtectionId,
    string ItemId,
    string NoticeItemId,
    string UserId,
    string? ItemIdentityHash,
    DateTime CreatedAtUtc,
    IReadOnlyList<string>? AffectedItemIds = null);

public sealed record ViewProtectionSeed(
    string ItemId,
    string NoticeItemId,
    string? ItemIdentityHash,
    IReadOnlyList<string>? AffectedItemIds = null);

public sealed record ProtectionMutationResult(bool Changed, LeavingSoonSnapshot Snapshot, ViewProtection? Protection = null);

public sealed record LeavingSoonSnapshot(
    long Revision,
    Guid? CollectionId,
    Guid? ReadyCollectionId,
    DateTime? LastSuccessfulRefreshUtc,
    string? LastRefreshError,
    IReadOnlyList<WarningEntry> Warnings,
    IReadOnlyList<ViewProtection> Protections);

public interface ILeavingSoonStateStore
{
    LeavingSoonSnapshot Read();
    long ReadRevision();
    LeavingSoonSnapshot SynchronizeCandidates(IReadOnlyList<LeavingSoonCandidate> candidates, DateTime nowUtc);
    LeavingSoonSnapshot MarkPublished(IReadOnlyCollection<string> itemIds, DateTime nowUtc);
    LeavingSoonSnapshot RecordPublicationError(IReadOnlyCollection<string> itemIds, string error);
    LeavingSoonSnapshot RecordRefreshSuccess(DateTime nowUtc);
    LeavingSoonSnapshot InvalidateWarnings();
    LeavingSoonSnapshot SetCollectionIds(Guid? collectionId, Guid? readyCollectionId);
    ProtectionMutationResult AddProtection(string itemId, string userId, string? identityHash, DateTime nowUtc);
    ProtectionMutationResult AddProtections(IReadOnlyCollection<ViewProtectionSeed> protections, string userId, DateTime nowUtc);
    LeavingSoonSnapshot MergeProtectionAffectedItemIds(IReadOnlyDictionary<string, IReadOnlyCollection<string>> affectedItemIds);
    ProtectionMutationResult RemoveProtection(string protectionId, string? ownerUserId, DateTime nowUtc);
    ProtectionMutationResult RemoveProtectionsForOwnerItem(string itemId, string ownerUserId, DateTime nowUtc);
    ProtectionMutationResult RemoveProtectionsForItem(string itemId, DateTime nowUtc);
    LeavingSoonSnapshot RemoveCompletedProtections(
        IReadOnlyCollection<string> protectionIds,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> affectedItemIds,
        DateTime nowUtc);
    LeavingSoonSnapshot MarkDeleted(IReadOnlyCollection<string> itemIds);
}

public interface ILeavingSoonPlaybackReader
{
    ProtectionPlaybackObservation Read(string itemId, string userId);
}
