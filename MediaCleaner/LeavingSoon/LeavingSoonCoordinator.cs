using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaCleaner.Adapters;
using MediaCleaner.Configuration;
using MediaCleaner.Core;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.LeavingSoon;

public sealed record LeavingSoonPreparation(
    IReadOnlyCollection<string> ProtectedItemIds,
    long StateRevision,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ProtectionReasons);

public enum CreateProtectionStatus { Created, Existing, AlreadyPlayed, NotCandidate }
public sealed record CreateProtectionResult(CreateProtectionStatus Status, ViewProtection? Protection, LeavingSoonSnapshot Snapshot);
public sealed record CreateProtectionBatchResult(CreateProtectionStatus Status, IReadOnlyList<ViewProtection> Protections, int SkippedPlayedCount, LeavingSoonSnapshot Snapshot);

public interface ILeavingSoonCollectionPublisher
{
    Task<Guid> SynchronizeAsync(Guid? collectionId, string collectionName, IReadOnlyCollection<string> itemIds, CleanupCatalog catalog, CancellationToken cancellationToken);
}

public sealed class LeavingSoonCoordinator(
    ILeavingSoonStateStore stateStore,
    ILeavingSoonCollectionPublisher collectionPublisher,
    ILeavingSoonPlaybackReader playbackReader,
    CleanupPlanner planner,
    IClock clock,
    ILogger<LeavingSoonCoordinator> logger)
{
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly ReaderWriterLockSlim _safetyLock = new(LockRecursionPolicy.NoRecursion);

    public LeavingSoonSnapshot ReadState() => stateStore.Read();
    public ProtectionPlaybackObservation ReadPlayback(string itemId, string userId) => playbackReader.Read(itemId, userId);

    public CreateProtectionResult AddProtection(string itemId, string userId)
    {
        var result = AddProtections(itemId, userId, [new ViewProtectionSeed(itemId, itemId, null)]);
        return new CreateProtectionResult(result.Status, result.Protections.FirstOrDefault(), result.Snapshot);
    }

    public CreateProtectionBatchResult AddProtections(string noticeItemId, string userId, IReadOnlyCollection<ViewProtectionSeed> requested)
    {
        return WithSafetyWriteLock(() =>
        {
            var snapshot = stateStore.Read();
            var warning = snapshot.Warnings.FirstOrDefault(x => IdEquals(x.ItemId, noticeItemId));
            if (warning is null) return new CreateProtectionBatchResult(CreateProtectionStatus.NotCandidate, [], 0, snapshot);
            var skipped = 0;
            var eligible = new List<ViewProtectionSeed>();
            foreach (var seed in requested)
            {
                if (playbackReader.Read(seed.ItemId, userId).Status == ProtectionPlaybackStatus.Played) { skipped++; continue; }
                eligible.Add(seed with { NoticeItemId = noticeItemId, ItemIdentityHash = seed.ItemIdentityHash ?? (IdEquals(seed.ItemId, noticeItemId) ? warning.IdentityHash : null) });
            }
            if (eligible.Count == 0) return new CreateProtectionBatchResult(CreateProtectionStatus.AlreadyPlayed, [], skipped, snapshot);
            var mutation = stateStore.AddProtections(eligible, userId, clock.UtcNow);
            var ids = eligible.Select(x => x.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var protections = mutation.Snapshot.Protections.Where(x => IdEquals(x.UserId, userId) && ids.Contains(x.ItemId)).ToList();
            return new CreateProtectionBatchResult(mutation.Changed ? CreateProtectionStatus.Created : CreateProtectionStatus.Existing, protections, skipped, mutation.Snapshot);
        });
    }

    public ProtectionMutationResult RemoveOwnProtection(string protectionId, string userId) =>
        WithSafetyWriteLock(() => stateStore.RemoveProtection(protectionId, userId, clock.UtcNow));

    public ProtectionMutationResult RemoveOwnItemProtections(string itemId, string userId) =>
        WithSafetyWriteLock(() => stateStore.RemoveProtectionsForOwnerItem(itemId, userId, clock.UtcNow));

    public ProtectionMutationResult RemoveProtectionAsAdmin(string protectionId) =>
        WithSafetyWriteLock(() => stateStore.RemoveProtection(protectionId, null, clock.UtcNow));

    public ProtectionMutationResult RemoveItemProtectionsAsAdmin(string itemId) =>
        WithSafetyWriteLock(() => stateStore.RemoveProtectionsForItem(itemId, clock.UtcNow));

    public LeavingSoonSnapshot MarkDeleted(IReadOnlyCollection<string> itemIds) =>
        WithSafetyWriteLock(() => stateStore.MarkDeleted(itemIds));
    public bool IsRevisionCurrent(long revision) => stateStore.ReadRevision() == revision;

    public void ExecuteDeletionIfRevisionCurrent(long revision, Action deletion)
    {
        _safetyLock.EnterReadLock();
        try
        {
            if (stateStore.ReadRevision() != revision)
                throw new OperationCanceledException("Leaving Soon safety state changed during cleanup; remaining deletions were aborted.");
            deletion();
        }
        finally { _safetyLock.ExitReadLock(); }
    }

    public Task<LeavingSoonPreparation> EvaluateReadOnlyAsync(
        CleanupPolicy policy,
        CleanupCatalog catalog,
        LeavingSoonConfiguration configuration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = stateStore.Read();
        var transition = EvaluateProtectionTransition(snapshot, catalog);
        var projected = ProjectProtectionTransition(snapshot, transition, clock.UtcNow);
        if (configuration.Enabled)
        {
            Validate(configuration);
            ValidateRuleNoticeDays(policy);
            var candidates = BuildCandidates(policy, catalog, configuration, projected);
            projected = ProjectPublishedCandidates(projected, candidates, clock.UtcNow);
        }

        var reasons = BuildProtectionReasons(projected, transition.Active, catalog, configuration, clock.UtcNow);
        return Task.FromResult(new LeavingSoonPreparation(reasons.Keys.ToList(), snapshot.Revision, ToReadOnly(reasons)));
    }

    public async Task<LeavingSoonPreparation> PrepareAsync(
        CleanupPolicy policy,
        CleanupCatalog catalog,
        LeavingSoonConfiguration configuration,
        CancellationToken cancellationToken)
    {
        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = RemovePlayedProtections(catalog);
            if (!configuration.Enabled)
            {
                if (snapshot.Warnings.Count > 0)
                    snapshot = WithSafetyWriteLock(stateStore.InvalidateWarnings);
                var disabledReasons = BuildProtectionReasons(snapshot, snapshot.Protections, catalog, configuration, clock.UtcNow);
                return new LeavingSoonPreparation(disabledReasons.Keys.ToList(), snapshot.Revision, ToReadOnly(disabledReasons));
            }

            Validate(configuration);
            ValidateRuleNoticeDays(policy);
            var candidates = BuildCandidates(policy, catalog, configuration, snapshot);

            snapshot = WithSafetyWriteLock(() => stateStore.SynchronizeCandidates(candidates, clock.UtcNow));
            try
            {
                var collectionId = await collectionPublisher.SynchronizeAsync(snapshot.CollectionId, configuration.CollectionName,
                    candidates.Select(x => x.ItemId).ToList(), catalog, cancellationToken).ConfigureAwait(false);
                snapshot = WithSafetyWriteLock(() => stateStore.SetCollectionIds(collectionId, snapshot.ReadyCollectionId));
                snapshot = WithSafetyWriteLock(() => stateStore.MarkPublished(candidates.Select(x => x.ItemId).ToList(), clock.UtcNow));
                snapshot = WithSafetyWriteLock(() => stateStore.RecordRefreshSuccess(clock.UtcNow));
            }
            catch (Exception ex)
            {
                WithSafetyWriteLock(() => stateStore.RecordPublicationError(candidates.Select(x => x.ItemId).ToList(), ex.Message));
                logger.LogError(ex, "Leaving Soon collection publication failed; notice-gated deletions are blocked.");
                throw;
            }

            var reasons = BuildProtectionReasons(snapshot, snapshot.Protections, catalog, configuration, clock.UtcNow);
            return new LeavingSoonPreparation(reasons.Keys.ToList(), snapshot.Revision, ToReadOnly(reasons));
        }
        finally { _runLock.Release(); }
    }

    public async Task RefreshDisabledAsync(CleanupCatalog catalog, LeavingSoonConfiguration configuration, CancellationToken cancellationToken)
    {
        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = RemovePlayedProtections(catalog);
            snapshot = WithSafetyWriteLock(stateStore.InvalidateWarnings);
            try
            {
                if (snapshot.CollectionId is not null)
                {
                    await collectionPublisher.SynchronizeAsync(snapshot.CollectionId, configuration.CollectionName, [], catalog, cancellationToken).ConfigureAwait(false);
                }
                WithSafetyWriteLock(() => stateStore.RecordRefreshSuccess(clock.UtcNow));
            }
            catch (Exception ex)
            {
                WithSafetyWriteLock(() => stateStore.RecordPublicationError([], ex.Message));
                throw;
            }
        }
        finally { _runLock.Release(); }
    }

    private LeavingSoonSnapshot RemovePlayedProtections(CleanupCatalog catalog)
    {
        return WithSafetyWriteLock(() =>
        {
            var snapshot = stateStore.Read();
            snapshot = stateStore.MergeProtectionAffectedItemIds(snapshot.Protections.ToDictionary(
                x => x.ProtectionId,
                x => (IReadOnlyCollection<string>)GetAffectedDeletionUnitIds(x, catalog).ToList(),
                StringComparer.OrdinalIgnoreCase));
            var transition = EvaluateProtectionTransition(snapshot, catalog);
            return transition.Completed.Count == 0
                ? snapshot
                : stateStore.RemoveCompletedProtections(
                    transition.Completed.Select(x => x.ProtectionId).ToList(),
                    transition.CompletedAffectedItemIds,
                    clock.UtcNow);
        });
    }

    private ProtectionTransition EvaluateProtectionTransition(LeavingSoonSnapshot snapshot, CleanupCatalog catalog)
    {
        var completed = snapshot.Protections
            .Where(x => playbackReader.Read(x.ItemId, x.UserId).Status == ProtectionPlaybackStatus.Played)
            .ToList();
        if (completed.Count == 0)
            return new ProtectionTransition([], snapshot.Protections,
                new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase));

        var completedIds = completed.Select(x => x.ProtectionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var active = snapshot.Protections.Where(x => !completedIds.Contains(x.ProtectionId)).ToList();
        var activeUnits = active.SelectMany(x => GetAffectedDeletionUnitIds(x, catalog))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var completedAffectedItemIds = completed.ToDictionary(
            x => x.ProtectionId,
            x => (IReadOnlyCollection<string>)GetAffectedDeletionUnitIds(x, catalog)
                .Where(id => !activeUnits.Contains(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            StringComparer.OrdinalIgnoreCase);
        return new ProtectionTransition(completed, active, completedAffectedItemIds);
    }

    private static LeavingSoonSnapshot ProjectProtectionTransition(
        LeavingSoonSnapshot snapshot,
        ProtectionTransition transition,
        DateTime nowUtc)
    {
        if (transition.Completed.Count == 0)
            return snapshot;

        var resetIds = transition.CompletedAffectedItemIds.Values.SelectMany(x => x).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var warnings = snapshot.Warnings.Select(warning => resetIds.Contains(warning.ItemId)
            ? warning with
            {
                Status = WarningStatus.PendingPublication,
                FirstDetectedUtc = nowUtc,
                FirstPublishedUtc = null,
                NotBeforeDeleteUtc = null,
                LastError = null,
            }
            : warning).ToList();
        return snapshot with { Warnings = warnings, Protections = transition.Active };
    }

    private static IEnumerable<string> GetAffectedDeletionUnitIds(ViewProtection protection, CleanupCatalog catalog)
    {
        var affected = new HashSet<string>(protection.AffectedItemIds ?? [], StringComparer.OrdinalIgnoreCase)
        {
            protection.ItemId,
            protection.NoticeItemId,
        };
        var direct = catalog.Items.FirstOrDefault(x => IdEquals(x.Id, protection.ItemId));
        if (!string.IsNullOrWhiteSpace(direct?.SeasonId)) affected.Add(direct.SeasonId);
        if (!string.IsNullOrWhiteSpace(direct?.SeriesId)) affected.Add(direct.SeriesId);

        foreach (var container in catalog.Items.Where(x => (x.EpisodeIds ?? []).Any(id => IdEquals(id, protection.ItemId))))
        {
            affected.Add(container.Id);
            if (!string.IsNullOrWhiteSpace(container.SeriesId)) affected.Add(container.SeriesId);
        }
        foreach (var series in catalog.Items.Where(x => (x.SeasonIds ?? []).Any(affected.Contains)))
            affected.Add(series.Id);
        return affected;
    }

    private sealed record ProtectionTransition(
        IReadOnlyList<ViewProtection> Completed,
        IReadOnlyList<ViewProtection> Active,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> CompletedAffectedItemIds);

    private Dictionary<string, List<string>> BuildProtectionReasons(
        LeavingSoonSnapshot snapshot,
        IReadOnlyList<ViewProtection> protections,
        CleanupCatalog catalog,
        LeavingSoonConfiguration configuration,
        DateTime nowUtc)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var protection in protections)
        {
            if (catalog.ItemsById.TryGetValue(protection.ItemId, out _) || catalog.Items.Any(x => IdEquals(x.Id, protection.ItemId)))
            {
                var current = catalog.Items.FirstOrDefault(x => IdEquals(x.Id, protection.ItemId));
                if (current is not null && protection.ItemIdentityHash is not null && !string.Equals(protection.ItemIdentityHash, IdentityHash(current), StringComparison.Ordinal))
                    continue;
            }
            var observation = playbackReader.Read(protection.ItemId, protection.UserId);
            var userDisplay = observation.UserName is null ? protection.UserId : $"{observation.UserName} ({protection.UserId})";
            var reason = observation.Status switch
            {
                ProtectionPlaybackStatus.UnknownUser => $"protected until watched; playback is unknown for deleted or unavailable user {protection.UserId}",
                ProtectionPlaybackStatus.MissingItem => $"personal protection retained for unavailable item (user {userDisplay})",
                ProtectionPlaybackStatus.UnavailableUserData => $"protected until watched; Jellyfin user data is unavailable for {userDisplay}",
                _ => $"protected until user {userDisplay} marks the item played (saved {protection.CreatedAtUtc:O})",
            };
            AddReason(result, protection.ItemId, reason);
            if (!IdEquals(protection.NoticeItemId, protection.ItemId))
                AddReason(result, protection.NoticeItemId,
                    $"deletion unit is protected by episode {protection.ItemId}: {reason}");
        }
        if (configuration.Enabled)
        {
            foreach (var warning in snapshot.Warnings.Where(x => x.FirstPublishedUtc is null || x.NotBeforeDeleteUtc is null || x.NotBeforeDeleteUtc > nowUtc))
            {
                AddReason(result, warning.ItemId, warning.FirstPublishedUtc is null
                    ? "Leaving Soon notice has not been successfully published"
                    : $"Leaving Soon notice is active until {warning.NotBeforeDeleteUtc:O}");
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ToReadOnly(Dictionary<string, List<string>> value) =>
        value.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value, StringComparer.OrdinalIgnoreCase);
    private static void AddReason(Dictionary<string, List<string>> result, string itemId, string reason)
    {
        if (!result.TryGetValue(itemId, out var reasons)) result[itemId] = reasons = [];
        reasons.Add(reason);
    }

    private List<LeavingSoonCandidate> BuildCandidates(
        CleanupPolicy policy,
        CleanupCatalog catalog,
        LeavingSoonConfiguration configuration,
        LeavingSoonSnapshot snapshot)
    {
        var generation = NormalizeNoticeGeneration(configuration.NoticeGeneration);
        var warningPolicy = BuildWarningPolicy(policy, configuration.NoticeDays, generation, snapshot);
        var warningPlan = planner.Plan(new CleanupRequest(warningPolicy, catalog.Users, catalog.Items, false));
        var rulesById = policy.Rules.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        // Announce only what the cascade would delete. A complete-season decision stays in
        // Decisions even when a protected episode blocks it.
        var deletableIds = warningPlan.Deletions.Select(x => x.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return warningPlan.Decisions.Where(decision => deletableIds.Contains(decision.Item.Id)).Select(decision => new LeavingSoonCandidate(
            decision.Item.Id,
            decision.Item.Kind,
            decision.Item.FullName,
            decision.Item.Path,
            IdentityHash(decision.Item),
            ConfigurationFingerprint(decision.MatchedRuleIds ?? [], rulesById, configuration.NoticeDays),
            decision.MatchedRuleIds ?? [],
            EffectiveNoticeDays(decision.MatchedRuleIds ?? [], rulesById, configuration.NoticeDays),
            generation)).ToList();
    }

    private static LeavingSoonSnapshot ProjectPublishedCandidates(
        LeavingSoonSnapshot snapshot,
        IReadOnlyList<LeavingSoonCandidate> candidates,
        DateTime nowUtc)
    {
        var existingById = snapshot.Warnings.ToDictionary(x => x.ItemId, StringComparer.OrdinalIgnoreCase);
        var warnings = new List<WarningEntry>(candidates.Count);
        foreach (var candidate in candidates)
        {
            existingById.TryGetValue(candidate.ItemId, out var existing);
            if (existing is not null
                && (!string.Equals(existing.IdentityHash, candidate.IdentityHash, StringComparison.Ordinal)
                    || !string.Equals(existing.NoticeGeneration, candidate.NoticeGeneration, StringComparison.Ordinal)))
                existing = null;

            DateTime firstDetectedUtc;
            DateTime firstPublishedUtc;
            DateTime notBeforeDeleteUtc;
            if (existing is null)
            {
                firstDetectedUtc = nowUtc;
                firstPublishedUtc = nowUtc;
                notBeforeDeleteUtc = nowUtc.AddDays(candidate.NoticeDays);
            }
            else
            {
                firstDetectedUtc = existing.FirstDetectedUtc;
                firstPublishedUtc = existing.FirstPublishedUtc ?? nowUtc;
                notBeforeDeleteUtc = existing.NotBeforeDeleteUtc ?? firstPublishedUtc.AddDays(candidate.NoticeDays);
                var proposed = firstPublishedUtc.AddDays(candidate.NoticeDays);
                if (proposed > notBeforeDeleteUtc)
                    notBeforeDeleteUtc = proposed;
            }

            warnings.Add(new WarningEntry(
                candidate.ItemId,
                candidate.Kind,
                candidate.Name,
                candidate.Path,
                candidate.IdentityHash,
                candidate.ConfigurationFingerprint,
                notBeforeDeleteUtc <= nowUtc ? WarningStatus.Ready : WarningStatus.Warning,
                firstDetectedUtc,
                firstPublishedUtc,
                notBeforeDeleteUtc,
                nowUtc,
                candidate.NoticeDays,
                candidate.RuleIds,
                null,
                candidate.NoticeGeneration));
        }

        return snapshot with { Warnings = warnings };
    }

    private static CleanupPolicy BuildWarningPolicy(CleanupPolicy policy, int defaultNoticeDays, string noticeGeneration, LeavingSoonSnapshot snapshot)
    {
        var promised = snapshot.Warnings.Where(x => x.FirstPublishedUtc is not null && string.Equals(x.NoticeGeneration, noticeGeneration, StringComparison.Ordinal))
            .SelectMany(x => x.RuleIds.Select(ruleId => (ruleId, x.NoticeDays)))
            .GroupBy(x => x.ruleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Max(y => y.NoticeDays), StringComparer.OrdinalIgnoreCase);
        return policy with { Rules = policy.Rules.Select(rule => rule.Actions.Kind != Core.CleanupRuleActionKind.Delete || rule.Trigger.Days < 0 ? rule : rule with
        {
            Trigger = rule.Trigger with { Days = Math.Max(0, rule.Trigger.Days - Math.Max(rule.Actions.NoticeDaysOverride ?? defaultNoticeDays, promised.GetValueOrDefault(rule.Id))) },
        }).ToList() };
    }

    private static string NormalizeNoticeGeneration(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "initial" : value;

    private static int EffectiveNoticeDays(IReadOnlyList<string> ruleIds, IReadOnlyDictionary<string, CleanupRule> rules, int defaultDays) =>
        ruleIds.Where(rules.ContainsKey).Select(id => rules[id]).Where(x => x.Actions.Kind == Core.CleanupRuleActionKind.Delete)
            .Select(x => x.Actions.NoticeDaysOverride ?? defaultDays).DefaultIfEmpty(0).Max();

    private static void Validate(LeavingSoonConfiguration configuration)
    {
        if (configuration.NoticeDays < 0) throw new InvalidOperationException("Leaving Soon notice days cannot be negative.");
        if (string.IsNullOrWhiteSpace(configuration.CollectionName)) throw new InvalidOperationException("Leaving Soon collection name cannot be empty.");
    }

    private static void ValidateRuleNoticeDays(CleanupPolicy policy)
    {
        if (policy.Rules.Any(rule => rule.Actions.NoticeDaysOverride < 0))
            throw new InvalidOperationException("A Leaving Soon per-rule notice override cannot be negative.");
    }

    internal static string IdentityHash(MediaItem item) => Hash(string.Join("\n", item.Kind, item.Path ?? string.Empty,
        item.DateCreated.ToUniversalTime().ToString("O"), item.SeriesId ?? string.Empty, item.SeasonId ?? string.Empty));
    internal static string IdentityHash(BaseItem item)
    {
        var kind = item switch
        {
            Episode => MediaItemKind.Episode,
            Season => MediaItemKind.Season,
            Series => MediaItemKind.Series,
            _ => item.GetType().Name switch
            {
                "Movie" => MediaItemKind.Movie,
                "Video" => MediaItemKind.Video,
                "Audio" => MediaItemKind.Audio,
                "AudioBook" => MediaItemKind.AudioBook,
                _ => MediaItemKind.Other,
            },
        };
        var seriesId = item switch { Episode episode => episode.SeriesId, Season season => season.SeriesId, Series series => series.Id, _ => Guid.Empty };
        var seasonId = item is Episode child ? child.SeasonId : item is Season parentSeason ? parentSeason.Id : Guid.Empty;
        return Hash(string.Join("\n", kind, item.Path ?? string.Empty, item.DateCreated.ToUniversalTime().ToString("O"),
            seriesId == Guid.Empty ? string.Empty : seriesId.ToString("N"), seasonId == Guid.Empty ? string.Empty : seasonId.ToString("N")));
    }
    private static string ConfigurationFingerprint(IReadOnlyList<string> ids, IReadOnlyDictionary<string, CleanupRule> rules, int defaultDays) =>
        Hash(string.Join("\n", ids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(id => rules.TryGetValue(id, out var rule)
            ? $"{id}:{rule.Trigger.Kind}:{rule.Trigger.Days}:{rule.Actions.NoticeDaysOverride ?? defaultDays}" : id)));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool IdEquals(string left, string right) => Guid.TryParse(left, out var l) && Guid.TryParse(right, out var r) ? l == r : string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private T WithSafetyWriteLock<T>(Func<T> action)
    {
        _safetyLock.EnterWriteLock();
        try { return action(); }
        finally { _safetyLock.ExitWriteLock(); }
    }
}
