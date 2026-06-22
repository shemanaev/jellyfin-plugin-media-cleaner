using System;
using System.Collections.Generic;
using System.Linq;

namespace MediaCleaner.Core;

internal sealed class DeletionCascadePlanner(IExtraFileProbe extraFileProbe)
{
    public IEnumerable<DeletionOperation> BuildDeletionOperations(
        IReadOnlyList<CleanupDecision> decisions,
        IReadOnlyList<MediaItem> items,
        ISet<string> protectedIds,
        List<CleanupAuditEntry> auditEntries)
    {
        var byId = items.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var decision in decisions)
        {
            foreach (var operation in BuildDeletionOperations(decision.Item, byId, deleted, protectedIds, auditEntries))
            {
                yield return operation;
            }
        }
    }

    private IEnumerable<DeletionOperation> BuildDeletionOperations(
        MediaItem item,
        IReadOnlyDictionary<string, MediaItem> byId,
        HashSet<string> deleted,
        ISet<string> protectedIds,
        List<CleanupAuditEntry> auditEntries)
    {
        var catalogItem = byId.TryGetValue(item.Id, out var foundItem) ? foundItem : item;
        if (item.Kind == MediaItemKind.Series && TryGetProtectedDescendant(catalogItem, byId, protectedIds, out var protectedChild))
        {
            CleanupAudit.AddCascadeBlocked(auditEntries, item, $"delete blocked because series contains protected item '{protectedChild.Name}'");
            yield break;
        }

        if (item.Kind == MediaItemKind.Season)
        {
            if (TryGetProtectedDescendant(catalogItem, byId, protectedIds, out protectedChild))
            {
                CleanupAudit.AddCascadeBlocked(auditEntries, item, $"delete blocked because season contains protected item '{protectedChild.Name}'");
                yield break;
            }

            foreach (var episodeId in catalogItem.EpisodeIds ?? [])
            {
                if (byId.TryGetValue(episodeId, out var episode))
                {
                    foreach (var op in AddDeletion(episode, deleted, protectedIds, auditEntries))
                    {
                        yield return op;
                    }
                }
            }
        }

        foreach (var op in AddDeletion(item, deleted, protectedIds, auditEntries))
        {
            yield return op;
        }

        if (item.Kind == MediaItemKind.Episode && item.SeasonId is not null && byId.TryGetValue(item.SeasonId, out var season))
        {
            var seasonEpisodeIds = season.EpisodeIds ?? [];
            if (seasonEpisodeIds.Count > 0 && seasonEpisodeIds.All(deleted.Contains))
            {
                if (TryGetProtectedDescendant(season, byId, protectedIds, out var protectedSeasonChild))
                {
                    CleanupAudit.AddCascadeBlocked(auditEntries, season, $"delete blocked because season contains protected item '{protectedSeasonChild.Name}'");
                }
                else
                {
                    foreach (var op in AddDeletion(season, deleted, protectedIds, auditEntries))
                    {
                        yield return op;
                    }
                }
            }
        }

        var seriesId = item.Kind == MediaItemKind.Series ? item.Id : item.SeriesId;
        if (seriesId is not null && byId.TryGetValue(seriesId, out var series))
        {
            var seriesEpisodeIds = series.EpisodeIds ?? [];
            if (seriesEpisodeIds.Count > 0 && seriesEpisodeIds.All(deleted.Contains))
            {
                if (extraFileProbe.HasBlockingExtraFiles(series))
                {
                    CleanupAudit.AddCascadeBlocked(auditEntries, series, "delete blocked because series has extra files outside planned episode deletions");
                }
                else if (TryGetProtectedDescendant(series, byId, protectedIds, out var protectedSeriesChild))
                {
                    CleanupAudit.AddCascadeBlocked(auditEntries, series, $"delete blocked because series contains protected item '{protectedSeriesChild.Name}'");
                }
                else
                {
                    foreach (var op in AddDeletion(series, deleted, protectedIds, auditEntries))
                    {
                        yield return op;
                    }
                }
            }
        }
    }

    private static bool TryGetProtectedDescendant(
        MediaItem item,
        IReadOnlyDictionary<string, MediaItem> byId,
        ISet<string> protectedIds,
        out MediaItem protectedChild)
    {
        foreach (var childId in GetDescendantIds(item, byId))
        {
            if (protectedIds.Contains(childId) && byId.TryGetValue(childId, out protectedChild!))
            {
                return true;
            }
        }

        protectedChild = null!;
        return false;
    }

    private static IEnumerable<string> GetDescendantIds(MediaItem item, IReadOnlyDictionary<string, MediaItem> byId)
    {
        foreach (var seasonId in item.SeasonIds ?? [])
        {
            yield return seasonId;
            if (byId.TryGetValue(seasonId, out var season))
            {
                foreach (var episodeId in season.EpisodeIds ?? [])
                {
                    yield return episodeId;
                }
            }
        }

        foreach (var episodeId in item.EpisodeIds ?? [])
        {
            yield return episodeId;
        }
    }

    private static IEnumerable<DeletionOperation> AddDeletion(
        MediaItem item,
        HashSet<string> deleted,
        ISet<string> protectedIds,
        List<CleanupAuditEntry> auditEntries)
    {
        if (protectedIds.Contains(item.Id))
        {
            CleanupAudit.AddCascadeBlocked(auditEntries, item, "delete blocked because item is protected");
            yield break;
        }

        if (deleted.Add(item.Id))
        {
            CleanupAudit.AddItem(
                auditEntries,
                item,
                null,
                CleanupAuditStage.DeletionCascade,
                CleanupAuditOutcome.Planned,
                $"planned deletion for {item.Kind.ToString().ToLowerInvariant()} '{CleanupAudit.GetItemDisplayName(item)}'",
                CleanupRuleActionKind.Delete);
            yield return new DeletionOperation(item.Id, item.Kind, item.Name);
        }
    }
}
