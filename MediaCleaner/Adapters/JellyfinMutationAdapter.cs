using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Activity;
using MediaCleaner.Compatibility;
using MediaCleaner.Core;
using MediaCleaner.LeavingSoon;
using CoreExpiredKind = MediaCleaner.Core.ExpiredKind;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.Adapters;

internal sealed class JellyfinMutationAdapter(
    ILogger<JellyfinMutationAdapter> logger,
    ILibraryManager libraryManager,
    IActivityManager activityManager,
    LeavingSoonCoordinator? leavingSoonCoordinator = null) : IMediaMutationAdapter
{
    public async Task ExecuteAsync(CleanupPlan plan, CleanupCatalog catalog, CancellationToken cancellationToken)
    {
        var plannedDeletionIds = plan.Deletions
            .Select(operation => operation.ItemId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var successfullyDeletedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var decisionsByItemId = GetDecisionsForItemIds(plan.Decisions, plannedDeletionIds)
            .ToDictionary(decision => decision.Item.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var operation in plan.Deletions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!catalog.ItemsById.TryGetValue(operation.ItemId, out var item))
            {
                logger.LogWarning("Skipping deletion for unknown item id {ItemId}", operation.ItemId);
                continue;
            }

            var deleted = false;
            void Delete()
            {
                // Jellyfin persists user data from MarkUnplayed, so it must run immediately
                // before deletion while the same safety revision is guarded.
                if (decisionsByItemId.TryGetValue(operation.ItemId, out var decision))
                {
                    MarkUnplayed(decision, catalog);
                }

                try
                {
                    JellyfinCompatibility.DeleteItem(libraryManager, item, new DeleteOptions { DeleteFileLocation = true });
                    deleted = true;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error deleting item: {Name}", operation.Name);
                }
            }

            if (plan.SafetyRevision is { } revision && leavingSoonCoordinator is not null)
            {
                leavingSoonCoordinator.ExecuteDeletionIfRevisionCurrent(revision, Delete);
            }
            else
            {
                Delete();
            }

            if (deleted)
            {
                successfullyDeletedIds.Add(operation.ItemId);
            }
        }

        foreach (var decision in GetDecisionsForItemIds(plan.Decisions, successfullyDeletedIds))
        {
            LogSuccessfulDeletion(decision);
            var notificationDecision = WithNotificationOverview(decision);
            await CreateNotification(notificationDecision, cancellationToken);
        }

        if (successfullyDeletedIds.Count > 0 && leavingSoonCoordinator is not null)
        {
            leavingSoonCoordinator.MarkDeleted(successfullyDeletedIds);
        }
    }

    internal static IEnumerable<CleanupDecision> GetDecisionsForItemIds(
        IEnumerable<CleanupDecision> decisions,
        ISet<string> itemIds) =>
        decisions.Where(decision => itemIds.Contains(decision.Item.Id));

    private void LogSuccessfulDeletion(CleanupDecision decision)
    {
        if (decision.Kind == CoreExpiredKind.Played)
        {
            var users = string.Join(", ", decision.Playback.Select(x => $"{(x.UserName ?? x.UserId)} ({x.LastPlayedDate?.ToLocalTime()})"));
            logger.LogInformation("({Type}) \"{Name}\" was deleted because expired for: {Users}",
                decision.Item.Kind, decision.Item.FullName, users);
            return;
        }

        if (decision.Kind == CoreExpiredKind.NotPlayed)
        {
            logger.LogInformation("({Type}) \"{Name}\" was deleted because no one played it since {DateCreated}",
                decision.Item.Kind, decision.Item.FullName, decision.Item.DateCreated.ToLocalTime());
            return;
        }

        logger.LogInformation("({Type}) \"{Name}\" was deleted because it was added at {DateCreated}",
            decision.Item.Kind, decision.Item.FullName, decision.Item.DateCreated.ToLocalTime());
    }

    private async Task CreateNotification(CleanupDecision decision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await activityManager.CreateAsync(
            JellyfinCompatibility.CreateActivityLog(
                decision.Notification.Title,
                "MediaCleaner",
                Guid.Empty,
                decision.Notification.ShortOverview,
                decision.Notification.Overview));
    }

    internal static CleanupDecision WithNotificationOverview(CleanupDecision decision)
    {
        return decision with
        {
            Notification = decision.Notification with
            {
                Overview = BuildNotificationOverview(decision),
            },
        };
    }

    internal static string BuildNotificationOverview(CleanupDecision decision)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Path:");
        builder.AppendLine(string.IsNullOrWhiteSpace(decision.Item.Path) ? "(no path)" : decision.Item.Path);
        builder.AppendLine();
        builder.AppendLine("Decision:");
        builder.Append("Reason: ");
        builder.AppendLine(decision.Reason);
        builder.Append("Matched rules: ");
        if (decision.MatchedRules.Count == 0)
        {
            builder.AppendLine("(none)");
        }
        else
        {
            builder.AppendLine(string.Join(", ", decision.MatchedRules));
        }

        builder.AppendLine("Result: successfully deleted");

        return builder.ToString().TrimEnd();
    }

    private static void MarkUnplayed(CleanupDecision decision, CleanupCatalog catalog)
    {
        if (decision.MarkUnplayedUserIds.Count == 0 || !catalog.ItemsById.TryGetValue(decision.Item.Id, out var item))
        {
            return;
        }

        foreach (var userId in decision.MarkUnplayedUserIds)
        {
            if (catalog.UsersById.TryGetValue(userId, out var user))
            {
                JellyfinCompatibility.MarkUnplayed(item, user);
            }
        }
    }
}
