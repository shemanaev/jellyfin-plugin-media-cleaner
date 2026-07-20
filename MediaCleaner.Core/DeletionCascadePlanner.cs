using System;
using System.Collections.Generic;
using System.Linq;

namespace MediaCleaner.Core;

internal sealed class DeletionCascadePlanner(IExtraFileProbe extraFileProbe)
{
    private CascadeState? _cascadeState;

    internal int EpisodeCounterUpdateCount => _cascadeState?.CounterUpdateCount ?? 0;

    public IEnumerable<DeletionOperation> BuildDeletionOperations(
        IReadOnlyList<CleanupDecision> decisions,
        IReadOnlyDictionary<string, MediaItem> byId,
        ISet<string> protectedIds,
        CleanupAuditCollector audit)
    {
        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cascadeState = CascadeState.Create(byId);
        _cascadeState = cascadeState;

        foreach (var decision in decisions)
        {
            foreach (var operation in BuildDeletionOperations(decision.Item, byId, deleted, protectedIds, audit, cascadeState))
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
        CleanupAuditCollector audit,
        CascadeState cascadeState)
    {
        var catalogItem = byId.TryGetValue(item.Id, out var foundItem) ? foundItem : item;
        if (item.Kind == MediaItemKind.Series && TryGetProtectedDescendant(catalogItem, byId, protectedIds, out var protectedChild))
        {
            CleanupAudit.AddCascadeBlocked(audit, item, $"delete blocked because series contains protected item '{protectedChild.Name}'");

            yield break;
        }

        if (item.Kind == MediaItemKind.Season)
        {
            if (TryGetProtectedDescendant(catalogItem, byId, protectedIds, out protectedChild))
            {
                CleanupAudit.AddCascadeBlocked(audit, item, $"delete blocked because season contains protected item '{protectedChild.Name}'");

                yield break;
            }

            foreach (var episodeId in catalogItem.EpisodeIds ?? [])
            {
                if (byId.TryGetValue(episodeId, out var episode))
                {
                    foreach (var op in AddDeletion(episode, deleted, protectedIds, audit, cascadeState))
                    {
                        yield return op;
                    }
                }
            }
        }

        foreach (var op in AddDeletion(item, deleted, protectedIds, audit, cascadeState))
        {
            yield return op;
        }

        if (item.Kind == MediaItemKind.Episode && item.SeasonId is not null && byId.TryGetValue(item.SeasonId, out var season))
        {
            if (cascadeState.HasEpisodes(season.Id) && cascadeState.RemainingEpisodes(season.Id) == 0)
            {
                if (TryGetProtectedDescendant(season, byId, protectedIds, out var protectedSeasonChild))
                {
                    CleanupAudit.AddCascadeBlocked(audit, season, $"delete blocked because season contains protected item '{protectedSeasonChild.Name}'");
                }
                else
                {
                    foreach (var op in AddDeletion(season, deleted, protectedIds, audit, cascadeState))
                    {
                        yield return op;
                    }
                }
            }
        }

        var seriesId = item.Kind == MediaItemKind.Series ? item.Id : item.SeriesId;
        if (seriesId is not null && byId.TryGetValue(seriesId, out var series))
        {
            if (cascadeState.HasEpisodes(series.Id) && cascadeState.RemainingEpisodes(series.Id) == 0)
            {
                if (extraFileProbe.HasBlockingExtraFiles(series))
                {
                    CleanupAudit.AddCascadeBlocked(audit, series, $"delete blocked because series has extra files outside planned episode deletions");
                }
                else if (TryGetProtectedDescendant(series, byId, protectedIds, out var protectedSeriesChild))
                {
                    CleanupAudit.AddCascadeBlocked(audit, series, $"delete blocked because series contains protected item '{protectedSeriesChild.Name}'");
                }
                else
                {
                    foreach (var op in AddDeletion(series, deleted, protectedIds, audit, cascadeState))
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
        CleanupAuditCollector audit,
        CascadeState cascadeState)
    {
        if (protectedIds.Contains(item.Id))
        {
            CleanupAudit.AddCascadeBlocked(audit, item, $"delete blocked because item is protected");

            yield break;
        }

        if (deleted.Add(item.Id))
        {
            if (item.Kind == MediaItemKind.Episode)
            {
                cascadeState.MarkEpisodeDeleted(item.Id);
            }

            CleanupAudit.AddItem(
                audit,
                item,
                null,
                CleanupAuditStage.DeletionCascade,
                CleanupAuditOutcome.Planned,
                $"planned deletion for {item.Kind} '{CleanupAudit.GetItemDisplayName(item)}'",
                CleanupRuleActionKind.Delete);

            yield return new DeletionOperation(item.Id, item.Kind, item.Name);
        }
    }

    private sealed class CascadeState
    {
        private readonly Dictionary<string, int> _remainingEpisodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _parentsWithEpisodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _parentsByEpisode = new(StringComparer.OrdinalIgnoreCase);

        public int CounterUpdateCount { get; private set; }

        public static CascadeState Create(IReadOnlyDictionary<string, MediaItem> byId)
        {
            var state = new CascadeState();
            foreach (var item in byId.Values)
            {
                if (item.Kind is not (MediaItemKind.Season or MediaItemKind.Series))
                {
                    continue;
                }

                var episodeIds = item.EpisodeIds;
                if (episodeIds is null || episodeIds.Count == 0)
                {
                    continue;
                }

                var uniqueEpisodeIds = episodeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
                state._parentsWithEpisodes.Add(item.Id);
                state._remainingEpisodes[item.Id] = uniqueEpisodeIds.Count;
                foreach (var episodeId in uniqueEpisodeIds)
                {
                    if (!state._parentsByEpisode.TryGetValue(episodeId, out var parents))
                    {
                        parents = [];
                        state._parentsByEpisode.Add(episodeId, parents);
                    }

                    parents.Add(item.Id);
                }
            }

            return state;
        }

        public bool HasEpisodes(string parentId) => _parentsWithEpisodes.Contains(parentId);

        public int RemainingEpisodes(string parentId) => _remainingEpisodes.GetValueOrDefault(parentId);

        public void MarkEpisodeDeleted(string episodeId)
        {
            if (!_parentsByEpisode.TryGetValue(episodeId, out var parents))
            {
                return;
            }

            foreach (var parentId in parents)
            {
                _remainingEpisodes[parentId]--;
                CounterUpdateCount++;
            }
        }
    }
}
