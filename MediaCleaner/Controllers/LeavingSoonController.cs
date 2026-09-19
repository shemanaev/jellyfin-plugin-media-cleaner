using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaCleaner.Compatibility;
using MediaCleaner.LeavingSoon;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MediaCleaner.Controllers;

public sealed record UserProtectionResponse(string ProtectionId, string ItemId, string NoticeItemId, string ItemName, string ItemKind, DateTime CreatedAtUtc, bool ItemAvailable);
public sealed record ProtectionCreateResponse(int ProtectionCount, int SkippedPlayedCount, IReadOnlyList<UserProtectionResponse> Protections);
public sealed record LeavingSoonItemResponse(string ItemId, string Name, string Kind, string Status, DateTime? DeleteAfterUtc,
    UserProtectionResponse? OwnProtection, bool ProtectedByOtherUser, bool CanProtect, int ProtectionTargetCount);
public sealed record LeavingSoonPageResponse(int StartIndex, int Limit, int TotalRecordCount, IReadOnlyList<LeavingSoonItemResponse> Items);
public sealed record LeavingSoonCardResponse(string ItemId, string Status, DateTime? DeleteAfterUtc);
public sealed record LeavingSoonCollectionCardsResponse(bool IsOwned, IReadOnlyList<LeavingSoonCardResponse> Items);
public sealed record UserProtectionPageResponse(int StartIndex, int Limit, int TotalRecordCount, IReadOnlyList<UserProtectionResponse> Items);
public sealed record LeavingSoonItemActionResponse(string ItemId, bool ShowAction, bool IsProtected, bool CanProtect, bool CanRemove, int ProtectionTargetCount,
    int ProtectedTargetCount, string? CollectionId, string? Status, DateTime? DeleteAfterUtc);
public sealed record AdminProtectionResponse(string ProtectionId, string ItemId, string NoticeItemId, string NoticeItemName, string ItemName, string ItemKind,
    string UserId, string UserName, DateTime CreatedAtUtc, bool ItemAvailable, int NoticeProtectionCount);
public sealed record AdminProtectionPageResponse(int StartIndex, int Limit, int TotalRecordCount, IReadOnlyList<AdminProtectionResponse> Items);
public sealed record LeavingSoonAdminStateResponse(long Revision, int WarningCount, int ProtectionCount, DateTime? LastSuccessfulRefreshUtc, string? LastRefreshError);

[Authorize]
[ApiController]
[Route("MediaCleaner/LeavingSoon")]
public sealed class LeavingSoonController(LeavingSoonCoordinator coordinator, ILibraryManager libraryManager, IUserManager userManager) : ControllerBase
{
    private const string JellyfinUserIdClaim = "Jellyfin-UserId";

    [HttpGet]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<LeavingSoonPageResponse> Get([FromQuery] int startIndex = 0, [FromQuery] int limit = 100, [FromQuery] string? search = null)
    {
        if (!TryGetCurrentUser(out var userId, out var user)) return Unauthorized();
        startIndex = Math.Max(0, startIndex); limit = Math.Clamp(limit, 1, 500);
        var snapshot = coordinator.ReadState();
        var currentProtections = snapshot.Protections.Where(IsCurrentProtection).ToList();
        var warnings = Plugin.Instance is not null && !Plugin.Instance.Configuration.LeavingSoon.Enabled ? [] : snapshot.Warnings;
        var visible = warnings.Where(entry => TryGetVisibleItem(entry.ItemId, user, out _, out _));
        if (!string.IsNullOrWhiteSpace(search)) visible = visible.Where(x => Contains(x.Name, search.Trim()) || Contains(x.Path, search.Trim()));
        var ordered = visible.OrderBy(x => x.NotBeforeDeleteUtc ?? DateTime.MaxValue).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var items = ordered.Skip(startIndex).Take(limit).Select(entry =>
        {
            var targets = GetProtectionTargets(entry.ItemId, user).ToList();
            var coverage = GetProtectionCoverage(targets.Select(x => x.Id.ToString("N")), currentProtections, userId);
            var isProtected = coverage.Total > 0 && coverage.Unprotected == 0;
            var protection = isProtected
                ? currentProtections.FirstOrDefault(x => IdEquals(x.UserId, userId) && ProtectsItem(x, entry.ItemId))
                : null;
            var canProtect = coverage.Unprotected > 0;
            var protectedByOtherUser = !isProtected && currentProtections.Any(x => !IdEquals(x.UserId, userId) && ProtectsItem(x, entry.ItemId));
            var status = isProtected ? "ProtectedUntilWatched"
                : protectedByOtherUser ? "ProtectedByOtherUser"
                : entry.FirstPublishedUtc is null ? "PendingPublication"
                : entry.NotBeforeDeleteUtc > DateTime.UtcNow ? "NoticeActive" : "Ready";
            return new LeavingSoonItemResponse(entry.ItemId, entry.Name, entry.Kind.ToString(), status, entry.NotBeforeDeleteUtc,
                protection is null ? null : MapOwnProtection(protection, user, entry), protectedByOtherUser, canProtect, coverage.Unprotected);
        }).ToList();
        return new LeavingSoonPageResponse(startIndex, limit, ordered.Count, items);
    }

    [HttpGet("Collections/{collectionId}/Cards")]
    public ActionResult<LeavingSoonCollectionCardsResponse> GetCollectionCards(string collectionId)
    {
        if (!TryGetCurrentUser(out var userId, out var user)) return Unauthorized();
        var snapshot = coordinator.ReadState();
        if (!Guid.TryParse(collectionId, out var parsedCollectionId) || snapshot.CollectionId != parsedCollectionId)
            return new LeavingSoonCollectionCardsResponse(false, []);

        var currentProtections = snapshot.Protections.Where(IsCurrentProtection).ToList();
        var warnings = Plugin.Instance is not null && !Plugin.Instance.Configuration.LeavingSoon.Enabled ? [] : snapshot.Warnings;
        var items = warnings
            .Where(entry => TryGetVisibleItem(entry.ItemId, user, out _, out _))
            .Select(entry =>
            {
                var targets = GetProtectionTargets(entry.ItemId, user).ToList();
                var coverage = GetProtectionCoverage(targets.Select(x => x.Id.ToString("N")), currentProtections, userId);
                var isProtected = coverage.Total > 0 && coverage.Unprotected == 0;
                var protectedByOtherUser = !isProtected && currentProtections.Any(x => !IdEquals(x.UserId, userId) && ProtectsItem(x, entry.ItemId));
                return new LeavingSoonCardResponse(
                    entry.ItemId,
                    isProtected ? "ProtectedUntilWatched"
                        : protectedByOtherUser ? "ProtectedByOtherUser"
                        : entry.FirstPublishedUtc is null ? "PendingPublication"
                        : entry.NotBeforeDeleteUtc > DateTime.UtcNow ? "NoticeActive" : "Ready",
                    entry.NotBeforeDeleteUtc);
            })
            .ToList();
        return new LeavingSoonCollectionCardsResponse(true, items);
    }

    [HttpGet("Protections/Mine")]
    public ActionResult<UserProtectionPageResponse> GetMine([FromQuery] int startIndex = 0, [FromQuery] int limit = 100)
    {
        if (!TryGetCurrentUser(out var userId, out var user)) return Unauthorized();
        startIndex = Math.Max(0, startIndex); limit = Math.Clamp(limit, 1, 500);
        var snapshot = coordinator.ReadState();
        var warnings = snapshot.Warnings.ToDictionary(x => x.ItemId, StringComparer.OrdinalIgnoreCase);
        var all = snapshot.Protections.Where(x => IdEquals(x.UserId, userId)).OrderBy(x => x.CreatedAtUtc).ToList();
        return new UserProtectionPageResponse(startIndex, limit, all.Count, all.Skip(startIndex).Take(limit)
            .Select(x => MapOwnProtection(x, user, warnings.GetValueOrDefault(x.ItemId))).ToList());
    }

    [HttpGet("{itemId}/Protection")]
    public ActionResult<LeavingSoonItemActionResponse> GetProtectionAction(string itemId)
    {
        if (!TryGetCurrentUser(out var userId, out var user)
            || !TryGetVisibleItem(itemId, user, out var normalizedId, out _))
            return NotFound();

        var snapshot = coordinator.ReadState();
        var currentProtections = snapshot.Protections.Where(IsCurrentProtection).ToList();
        var targets = GetProtectionTargets(normalizedId, user).ToList();
        var coverage = GetProtectionCoverage(targets.Select(x => x.Id.ToString("N")), currentProtections, userId);
        var protectedTargetCount = coverage.Protected;
        var unprotectedTargetCount = coverage.Unprotected;
        var isProtected = coverage.Total > 0 && unprotectedTargetCount == 0;
        var canRemove = HasProtectionForItem(currentProtections, userId, normalizedId);
        var warning = Plugin.Instance is not null && !Plugin.Instance.Configuration.LeavingSoon.Enabled
            ? null
            : snapshot.Warnings.FirstOrDefault(x => IdEquals(x.ItemId, normalizedId));
        var isCandidate = warning is not null;
        var protectedByOtherUser = !isProtected && isCandidate && currentProtections.Any(x => !IdEquals(x.UserId, userId)
            && ProtectsItem(x, normalizedId));
        var targetCount = isCandidate ? unprotectedTargetCount : 0;
        var canProtect = isCandidate && targetCount > 0;
        var status = isProtected ? "ProtectedUntilWatched"
            : protectedByOtherUser ? "ProtectedByOtherUser"
            : warning is null ? null
            : warning.FirstPublishedUtc is null ? "PendingPublication"
            : warning.NotBeforeDeleteUtc > DateTime.UtcNow ? "NoticeActive" : "Ready";
        return new LeavingSoonItemActionResponse(normalizedId, canRemove || isCandidate, isProtected, canProtect, canRemove, targetCount, protectedTargetCount,
            snapshot.CollectionId?.ToString("N"), status, warning?.NotBeforeDeleteUtc);
    }

    [HttpPut("{itemId}/Protection")]
    public ActionResult<ProtectionCreateResponse> Protect(string itemId)
    {
        if (!TryGetCurrentUser(out var userId, out var user) || !TryGetVisibleItem(itemId, user, out var normalizedId, out _)) return NotFound();
        if (Plugin.Instance is not null && !Plugin.Instance.Configuration.LeavingSoon.Enabled) return Conflict(new { error = "Leaving Soon is disabled." });
        var targets = GetProtectionTargets(normalizedId, user).ToList();
        if (targets.Count == 0) return Conflict(new { error = "There are no visible unplayed items to protect. Mark the item unplayed in Jellyfin before saving it for a rewatch." });
        var seeds = targets.Select(x => new ViewProtectionSeed(
            x.Id.ToString("N"),
            normalizedId,
            LeavingSoonCoordinator.IdentityHash(x),
            GetAffectedItemIds(x, normalizedId))).ToList();
        var result = coordinator.AddProtections(normalizedId, userId, seeds);
        if (result.Status == CreateProtectionStatus.AlreadyPlayed) return Conflict(new { error = "The item is already marked played for this user." });
        if (result.Status == CreateProtectionStatus.NotCandidate || result.Protections.Count == 0) return Conflict(new { error = "The item is no longer a Leaving Soon candidate." });
        var warnings = result.Snapshot.Warnings.ToDictionary(x => x.ItemId, StringComparer.OrdinalIgnoreCase);
        return new ProtectionCreateResponse(result.Protections.Count, result.SkippedPlayedCount,
            result.Protections.Select(x => MapOwnProtection(x, user, warnings.GetValueOrDefault(x.NoticeItemId))).ToList());
    }

    [HttpDelete("Protections/{protectionId}")]
    public ActionResult RemoveOwn(string protectionId)
    {
        if (!Guid.TryParse(protectionId, out _) || !TryGetCurrentUser(out var userId, out _)) return BadRequest();
        return coordinator.RemoveOwnProtection(protectionId, userId).Changed ? NoContent() : NotFound();
    }

    [HttpDelete("Protections/Mine/Items/{itemId}")]
    public ActionResult RemoveOwnForItem(string itemId)
    {
        if (!Guid.TryParse(itemId, out var id) || !TryGetCurrentUser(out var userId, out _)) return BadRequest();
        return coordinator.RemoveOwnItemProtections(id.ToString("N"), userId).Changed ? NoContent() : NotFound();
    }

    private UserProtectionResponse MapOwnProtection(ViewProtection protection, JellyfinUser user, WarningEntry? warning)
    {
        BaseItem? item = null;
        var visible = TryGetVisibleItem(protection.ItemId, user, out _, out item);
        var available = visible && (protection.ItemIdentityHash is null || string.Equals(protection.ItemIdentityHash, LeavingSoonCoordinator.IdentityHash(item!), StringComparison.Ordinal));
        return new UserProtectionResponse(protection.ProtectionId, protection.ItemId, protection.NoticeItemId,
            available ? item!.Name : "Unavailable item", available ? item!.GetType().Name : "Unknown", protection.CreatedAtUtc, available);
    }

    private IEnumerable<BaseItem> GetProtectionTargets(string itemId, JellyfinUser user)
    {
        if (!Guid.TryParse(itemId, out var id) || libraryManager.GetItemById(id) is not { } item) return [];
        IEnumerable<BaseItem> targets = item switch
        {
            Season season => season.GetEpisodes().Cast<BaseItem>(),
            Series series => JellyfinCompatibility.GetEpisodes(series),
            _ => [item],
        };
        return targets.Where(x => !x.IsVirtualItem && x.IsVisible(user, true)
            && coordinator.ReadPlayback(x.Id.ToString("N"), user.Id.ToString("N")).Status != ProtectionPlaybackStatus.Played);
    }

    private static IReadOnlyList<string> GetAffectedItemIds(BaseItem item, string noticeItemId)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            item.Id.ToString("N"),
            noticeItemId,
        };
        if (item is Episode episode)
        {
            if (episode.SeasonId != Guid.Empty) ids.Add(episode.SeasonId.ToString("N"));
            if (episode.SeriesId != Guid.Empty) ids.Add(episode.SeriesId.ToString("N"));
        }
        else if (item is Season season && season.SeriesId != Guid.Empty)
        {
            ids.Add(season.SeriesId.ToString("N"));
        }
        return ids.ToList();
    }

    private bool IsCurrentProtection(ViewProtection protection)
    {
        if (!Guid.TryParse(protection.ItemId, out var itemId) || libraryManager.GetItemById(itemId) is not { } item)
            return false;
        return protection.ItemIdentityHash is null
            || string.Equals(protection.ItemIdentityHash, LeavingSoonCoordinator.IdentityHash(item), StringComparison.Ordinal);
    }

    private static bool ProtectsItem(ViewProtection protection, string itemId) =>
        AffectedIds(protection).Any(id => IdEquals(id, itemId));

    private static IEnumerable<string> AffectedIds(ViewProtection protection) =>
        (protection.AffectedItemIds ?? []).Append(protection.ItemId).Append(protection.NoticeItemId)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    internal static (int Total, int Protected, int Unprotected) GetProtectionCoverage(
        IEnumerable<string> targetItemIds,
        IEnumerable<ViewProtection> protections,
        string userId)
    {
        var targetIds = targetItemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var protectedIds = protections
            .Where(x => IdEquals(x.UserId, userId) && targetIds.Contains(x.ItemId))
            .Select(x => x.ItemId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (targetIds.Count, protectedIds.Count, targetIds.Count - protectedIds.Count);
    }

    internal static bool HasProtectionForItem(IEnumerable<ViewProtection> protections, string userId, string itemId) =>
        protections.Any(x => IdEquals(x.UserId, userId) && ProtectsItem(x, itemId));

    private bool TryGetCurrentUser(out string userId, out JellyfinUser user)
    {
        var claim = User.FindFirst(JellyfinUserIdClaim)?.Value;
        if (Guid.TryParse(claim, out var id) && userManager.GetUserById(id) is { } found)
        { userId = id.ToString("N"); user = found; return true; }
        userId = string.Empty; user = null!; return false;
    }

    private bool TryGetVisibleItem(string itemId, JellyfinUser user, out string normalizedItemId, out BaseItem? item)
    {
        normalizedItemId = string.Empty; item = null;
        if (!Guid.TryParse(itemId, out var id) || libraryManager.GetItemById(id) is not { } found || !found.IsVisible(user, true)) return false;
        normalizedItemId = id.ToString("N"); item = found; return true;
    }

    private static bool IdEquals(string left, string right) => Guid.TryParse(left, out var l) && Guid.TryParse(right, out var r) && l == r;
    private static bool Contains(string? value, string term) => value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
}

[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Route("MediaCleaner/LeavingSoon/Admin")]
public sealed class LeavingSoonAdminController(
    LeavingSoonCoordinator coordinator,
    ILibraryManager libraryManager,
    IUserManager userManager,
    ILeavingSoonRefresher refresher) : ControllerBase
{
    [HttpGet("State")]
    public LeavingSoonAdminStateResponse GetState()
    {
        var state = coordinator.ReadState();
        return new LeavingSoonAdminStateResponse(state.Revision, state.Warnings.Count, state.Protections.Count, state.LastSuccessfulRefreshUtc, state.LastRefreshError);
    }

    [HttpPost("Refresh")]
    public async Task<ActionResult<LeavingSoonAdminStateResponse>> Refresh(CancellationToken cancellationToken)
    {
        await refresher.RefreshAsync(null, cancellationToken).ConfigureAwait(false);
        return GetState();
    }

    [HttpGet("Protections")]
    public ActionResult<AdminProtectionPageResponse> GetProtections([FromQuery] int startIndex = 0, [FromQuery] int limit = 100, [FromQuery] string? search = null)
    {
        startIndex = Math.Max(0, startIndex); limit = Math.Clamp(limit, 1, 500);
        var snapshot = coordinator.ReadState();
        var warnings = snapshot.Warnings.ToDictionary(x => x.ItemId, StringComparer.OrdinalIgnoreCase);
        var counts = snapshot.Protections.Select(x => x.NoticeItemId).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(id => id, id => snapshot.Protections.Count(x => ProtectsItem(x, id)), StringComparer.OrdinalIgnoreCase);
        var mapped = snapshot.Protections
            .Select(x => Map(x, warnings.GetValueOrDefault(x.NoticeItemId), counts.GetValueOrDefault(x.NoticeItemId)))
            .ToList();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            mapped = mapped.Where(x => Contains(x.NoticeItemName, term) || Contains(x.ItemName, term) || Contains(x.UserName, term) || Contains(x.ItemId, term)).ToList();
        }
        mapped = mapped.OrderBy(x => x.ItemName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.UserName, StringComparer.OrdinalIgnoreCase).ToList();
        return new AdminProtectionPageResponse(startIndex, limit, mapped.Count, mapped.Skip(startIndex).Take(limit).ToList());
    }

    [HttpDelete("Protections/{protectionId}")]
    public ActionResult Remove(string protectionId) => Guid.TryParse(protectionId, out _) && coordinator.RemoveProtectionAsAdmin(protectionId).Changed ? NoContent() : NotFound();

    [HttpDelete("Items/{itemId}/Protections")]
    public ActionResult RemoveAll(string itemId) => Guid.TryParse(itemId, out _) && coordinator.RemoveItemProtectionsAsAdmin(itemId).Changed ? NoContent() : NotFound();

    private AdminProtectionResponse Map(ViewProtection protection, WarningEntry? warning, int noticeProtectionCount)
    {
        var item = Guid.TryParse(protection.ItemId, out var itemId) ? libraryManager.GetItemById(itemId) : null;
        var identityMatches = item is not null && (protection.ItemIdentityHash is null || string.Equals(protection.ItemIdentityHash, LeavingSoonCoordinator.IdentityHash(item), StringComparison.Ordinal));
        var itemAvailable = identityMatches && item is not null;
        var userName = Guid.TryParse(protection.UserId, out var userId) && userManager.GetUserById(userId) is { } user
            ? user.Username : $"Unknown or deleted user ({protection.UserId})";
        return new AdminProtectionResponse(protection.ProtectionId, protection.ItemId, protection.NoticeItemId,
            warning?.Name ?? (IdEquals(protection.ItemId, protection.NoticeItemId) && itemAvailable ? item!.Name : "Unavailable item"),
            itemAvailable ? item!.Name : "Unavailable item", itemAvailable ? item!.GetType().Name : "Unknown",
            protection.UserId, userName, protection.CreatedAtUtc, itemAvailable, noticeProtectionCount);
    }
    private static bool IdEquals(string left, string right) => Guid.TryParse(left, out var l) && Guid.TryParse(right, out var r) && l == r;
    private static bool Contains(string? value, string term) => value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
    private static bool ProtectsItem(ViewProtection protection, string itemId) =>
        (protection.AffectedItemIds ?? []).Append(protection.ItemId).Append(protection.NoticeItemId)
            .Any(id => IdEquals(id, itemId));
}
