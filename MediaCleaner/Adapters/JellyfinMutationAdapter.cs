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
using CoreExpiredKind = MediaCleaner.Core.ExpiredKind;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.Adapters;

internal sealed class JellyfinMutationAdapter(
    ILogger<JellyfinMutationAdapter> logger,
    ILibraryManager libraryManager,
    IActivityManager activityManager) : IMediaMutationAdapter
{
    public async Task ExecuteAsync(CleanupPlan plan, CleanupCatalog catalog, CancellationToken cancellationToken)
    {
        foreach (var decision in plan.Decisions)
        {
            LogDecision(decision);
            if (plan.Deletions.Count == 0)
            {
                continue;
            }

            var notificationDecision = WithNotificationOverview(decision, plan.AuditEntries);
            await CreateNotification(notificationDecision, cancellationToken);
            MarkUnplayed(decision, catalog);
        }

        foreach (var operation in plan.Deletions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!catalog.ItemsById.TryGetValue(operation.ItemId, out var item))
            {
                logger.LogWarning("Skipping deletion for unknown item id {ItemId}", operation.ItemId);
                continue;
            }

            try
            {
                JellyfinCompatibility.DeleteItem(libraryManager, item, new DeleteOptions { DeleteFileLocation = true });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error deleting item: {Name}", operation.Name);
            }
        }
    }

    private void LogDecision(CleanupDecision decision)
    {
        if (decision.Kind == CoreExpiredKind.Played)
        {
            var users = string.Join(", ", decision.Playback.Select(x => $"{(x.UserName ?? x.UserId)} ({x.LastPlayedDate?.ToLocalTime()})"));
            logger.LogInformation("({Type}) \"{Name}\" will be deleted because expired for: {Users}",
                decision.Item.Kind, decision.Item.FullName, users);
            return;
        }

        if (decision.Kind == CoreExpiredKind.NotPlayed)
        {
            logger.LogInformation("({Type}) \"{Name}\" will be deleted because no one played it since {DateCreated}",
                decision.Item.Kind, decision.Item.FullName, decision.Item.DateCreated.ToLocalTime());
            return;
        }

        logger.LogInformation("({Type}) \"{Name}\" will be deleted because it was added at {DateCreated}",
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

    internal static CleanupDecision WithNotificationOverview(CleanupDecision decision, IReadOnlyList<CleanupAuditEntry> auditEntries)
    {
        return decision with
        {
            Notification = decision.Notification with
            {
                Overview = BuildNotificationOverview(decision, auditEntries),
            },
        };
    }

    internal static string BuildNotificationOverview(CleanupDecision decision, IReadOnlyList<CleanupAuditEntry> auditEntries)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Path:");
        builder.AppendLine(string.IsNullOrWhiteSpace(decision.Item.Path) ? "(no path)" : decision.Item.Path);
        builder.AppendLine();
        builder.AppendLine("Decision log:");

        var itemEntries = CleanupAuditFormatter.GetItemEntries(auditEntries, decision.Item.Id);
        if (itemEntries.Count == 0)
        {
            builder.AppendLine("- No item-level audit entries were produced.");
            return builder.ToString().TrimEnd();
        }

        foreach (var entry in itemEntries)
        {
            builder.Append("- ");
            builder.AppendLine(CleanupAuditFormatter.FormatPlainTextEntry(entry, includeAction: true));
        }

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
