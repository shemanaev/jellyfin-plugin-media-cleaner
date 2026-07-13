using System;
using System.Collections.Generic;
using System.Linq;

namespace MediaCleaner.Core;

internal static class SeriesPolicyEvaluator
{
    public static IEnumerable<CandidateItem> Apply(
        IEnumerable<CandidateItem> candidates,
        CleanupRule rule,
        List<CleanupAuditEntry> auditEntries,
        IReadOnlyList<MediaItem> catalogItems)
    {
        var episodes = new List<CandidateItem>();
        foreach (var item in candidates)
        {
            if (item.Item.Kind == MediaItemKind.Episode)
            {
                episodes.Add(item);
            }
            else
            {
                yield return item;
            }
        }

        if (episodes.Count == 0)
        {
            yield break;
        }

        IReadOnlyDictionary<string, MediaItem>? catalogById = null;
        IReadOnlyDictionary<string, MediaItem> GetCatalogById() =>
            catalogById ??= catalogItems.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var seriesItems = rule.Filters.DeleteEpisodes switch
        {
            SeriesDeleteKind.Episode => KeepEpisodes(episodes, rule, auditEntries),
            SeriesDeleteKind.Season => BuildSeasonCandidates(episodes, rule, auditEntries, GetCatalogById()),
            SeriesDeleteKind.Series => BuildSeriesCandidates(episodes, rule, auditEntries, GetCatalogById(), requireEnded: false),
            SeriesDeleteKind.SeriesEnded => BuildSeriesCandidates(episodes, rule, auditEntries, GetCatalogById(), requireEnded: true),
            _ => throw new NotSupportedException($"Unsupported series delete kind: {rule.Filters.DeleteEpisodes}"),
        };

        foreach (var item in seriesItems)
        {
            yield return item;
        }
    }

    private static IEnumerable<CandidateItem> KeepEpisodes(
        IEnumerable<CandidateItem> items,
        CleanupRule rule,
        List<CleanupAuditEntry> auditEntries)
    {
        if (rule.Filters.KeepSeriesKind == SeriesKeepKind.None)
        {
            foreach (var item in items)
            {
                yield return item;
            }

            yield break;
        }

        if (rule.Filters.KeepSeriesKind is not SeriesKeepKind.First and not SeriesKeepKind.Last)
        {
            throw new NotSupportedException($"Unsupported series keep kind: {rule.Filters.KeepSeriesKind}");
        }

        var boundaryName = rule.Filters.KeepSeriesKind == SeriesKeepKind.First ? "first" : "latest";
        foreach (var group in items.GroupBy(x => x.Item.SeriesId ?? x.Item.Id))
        {
            var groupItems = group.ToList();
            var boundaryIds = groupItems
                .Select(x => rule.Filters.KeepSeriesKind == SeriesKeepKind.First
                    ? x.Item.FirstEpisodeId
                    : x.Item.LastEpisodeId)
                .ToList();
            var distinctBoundaryIds = boundaryIds
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (boundaryIds.Any(string.IsNullOrWhiteSpace) || distinctBoundaryIds.Count != 1)
            {
                foreach (var item in groupItems)
                {
                    CleanupAudit.AddItem(
                        auditEntries,
                        item.Item,
                        rule,
                        CleanupAuditStage.SeriesPolicy,
                        CleanupAuditOutcome.Blocked,
                        $"delete blocked by series policy because the {boundaryName} episode in series '{group.Key}' could not be determined consistently");
                }

                continue;
            }

            var boundaryId = distinctBoundaryIds[0];
            var keptCandidate = false;
            foreach (var item in groupItems)
            {
                if (string.Equals(item.Item.Id, boundaryId, StringComparison.OrdinalIgnoreCase))
                {
                    keptCandidate = true;
                    CleanupAudit.AddItem(
                        auditEntries,
                        item.Item,
                        rule,
                        CleanupAuditStage.SeriesPolicy,
                        CleanupAuditOutcome.Rejected,
                        $"rejected by series policy because the {boundaryName} episode '{boundaryId}' is kept");
                    continue;
                }

                yield return item;
            }

            if (!keptCandidate)
            {
                CleanupAudit.AddRule(
                    auditEntries,
                    rule,
                    CleanupAuditStage.SeriesPolicy,
                    CleanupAuditOutcome.Skipped,
                    $"series policy keeps the {boundaryName} episode '{boundaryId}' in series '{group.Key}'; that episode did not match this rule");
            }
        }
    }

    private static DateTime? FirstPlaybackLastPlayedDate(IReadOnlyList<PlaybackState> playback)
    {
        return playback.Count == 0 ? null : playback[0].LastPlayedDate;
    }

    private static IEnumerable<CandidateItem> BuildSeasonCandidates(
        IEnumerable<CandidateItem> items,
        CleanupRule rule,
        List<CleanupAuditEntry> auditEntries,
        IReadOnlyDictionary<string, MediaItem> catalogById)
    {
        if (rule.Filters.KeepSeriesKind is not SeriesKeepKind.None and not SeriesKeepKind.First and not SeriesKeepKind.Last)
        {
            throw new NotSupportedException($"Unsupported series keep kind: {rule.Filters.KeepSeriesKind}");
        }

        foreach (var group in items.GroupBy(x => x.Item.SeasonId ?? x.Item.SeriesId ?? x.Item.Id))
        {
            var first = group.MaxBy(x => FirstPlaybackLastPlayedDate(x.Playback) ?? x.Item.DateCreated);
            if (first is null || first.Item.SeasonId is null)
            {
                if (first is not null)
                {
                    CleanupAudit.AddItem(
                        auditEntries,
                        first.Item,
                        rule,
                        CleanupAuditStage.SeriesPolicy,
                        CleanupAuditOutcome.Rejected,
                        "rejected by series policy because season id is missing");
                }

                continue;
            }

            catalogById.TryGetValue(first.Item.SeasonId, out var catalogSeason);
            var seasonEpisodes = catalogSeason?.EpisodeIds ?? first.Item.SeasonEpisodeIds ?? first.Item.EpisodeIds ?? [];
            var allWatched = seasonEpisodes.Count > 0
                && group.Select(x => x.Item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase).IsSupersetOf(seasonEpisodes);
            if (!allWatched)
            {
                CleanupAudit.AddItem(
                    auditEntries,
                    first.Item,
                    rule,
                    CleanupAuditStage.SeriesPolicy,
                    CleanupAuditOutcome.Rejected,
                    "rejected by series policy because not every season episode matched");
                continue;
            }

            var boundaryName = rule.Filters.KeepSeriesKind == SeriesKeepKind.First ? "first" : "latest";
            var boundaryIds = group
                .Select(x => rule.Filters.KeepSeriesKind == SeriesKeepKind.First
                    ? x.Item.FirstSeasonId
                    : x.Item.LastSeasonId)
                .ToList();
            var distinctBoundaryIds = boundaryIds
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (rule.Filters.KeepSeriesKind != SeriesKeepKind.None
                && (boundaryIds.Any(string.IsNullOrWhiteSpace) || distinctBoundaryIds.Count != 1))
            {
                CleanupAudit.AddItem(
                    auditEntries,
                    first.Item,
                    rule,
                    CleanupAuditStage.SeriesPolicy,
                    CleanupAuditOutcome.Blocked,
                    $"delete blocked by series policy because the {boundaryName} season in series '{first.Item.SeriesId}' could not be determined consistently");
                continue;
            }

            if (rule.Filters.KeepSeriesKind != SeriesKeepKind.None
                && string.Equals(first.Item.SeasonId, distinctBoundaryIds[0], StringComparison.OrdinalIgnoreCase))
            {
                CleanupAudit.AddItem(
                    auditEntries,
                    first.Item,
                    rule,
                    CleanupAuditStage.SeriesPolicy,
                    CleanupAuditOutcome.Rejected,
                    $"rejected by series policy because the {boundaryName} season '{distinctBoundaryIds[0]}' is kept");
                continue;
            }

            var seasonItem = catalogSeason ?? first.Item;
            var candidate = new CandidateItem(
                seasonItem with
                {
                    Id = first.Item.SeasonId,
                    Kind = MediaItemKind.Season,
                    Name = catalogSeason?.Name ?? first.Item.SeasonName ?? first.Item.Name,
                    FullName = catalogSeason?.FullName ?? $"{first.Item.SeriesName} | S{first.Item.ParentIndexNumber:D2} | {first.Item.SeasonName ?? first.Item.Name}",
                    IndexNumber = catalogSeason?.IndexNumber ?? first.Item.ParentIndexNumber,
                    EpisodeIds = seasonEpisodes,
                },
                first.Playback);

            CleanupAudit.AddItem(
                auditEntries,
                candidate.Item,
                rule,
                CleanupAuditStage.SeriesPolicy,
                CleanupAuditOutcome.Matched,
                "matched season series policy because every season episode matched");
            yield return candidate;
        }
    }

    private static IEnumerable<CandidateItem> BuildSeriesCandidates(
        IEnumerable<CandidateItem> items,
        CleanupRule rule,
        List<CleanupAuditEntry> auditEntries,
        IReadOnlyDictionary<string, MediaItem> catalogById,
        bool requireEnded)
    {
        foreach (var group in items.GroupBy(x => x.Item.SeriesId ?? x.Item.Id))
        {
            var first = group.MaxBy(x => FirstPlaybackLastPlayedDate(x.Playback) ?? x.Item.DateCreated);
            if (first is null || first.Item.SeriesId is null)
            {
                if (first is not null)
                {
                    CleanupAudit.AddItem(
                        auditEntries,
                        first.Item,
                        rule,
                        CleanupAuditStage.SeriesPolicy,
                        CleanupAuditOutcome.Rejected,
                        "rejected by series policy because series id is missing");
                }

                continue;
            }

            catalogById.TryGetValue(first.Item.SeriesId, out var catalogSeries);
            var seriesEpisodes = catalogSeries?.EpisodeIds ?? first.Item.SeriesEpisodeIds ?? first.Item.EpisodeIds ?? [];
            var allWatched = seriesEpisodes.Count > 0
                && group.Select(x => x.Item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase).IsSupersetOf(seriesEpisodes);
            if (!allWatched)
            {
                CleanupAudit.AddItem(
                    auditEntries,
                    first.Item,
                    rule,
                    CleanupAuditStage.SeriesPolicy,
                    CleanupAuditOutcome.Rejected,
                    "rejected by series policy because not every series episode matched");
                continue;
            }

            if (requireEnded && first.Item.SeriesStatus is not MediaSeriesStatus.Ended and not MediaSeriesStatus.Unknown)
            {
                CleanupAudit.AddItem(
                    auditEntries,
                    first.Item,
                    rule,
                    CleanupAuditStage.SeriesPolicy,
                    CleanupAuditOutcome.Rejected,
                    "rejected by series policy because series is continuing");
                continue;
            }

            var seriesItem = catalogSeries ?? first.Item;
            var candidate = new CandidateItem(
                seriesItem with
                {
                    Id = first.Item.SeriesId,
                    Kind = MediaItemKind.Series,
                    Name = catalogSeries?.Name ?? first.Item.SeriesName ?? first.Item.Name,
                    FullName = catalogSeries?.FullName ?? first.Item.SeriesName ?? first.Item.Name,
                    EpisodeIds = seriesEpisodes,
                },
                first.Playback);

            CleanupAudit.AddItem(
                auditEntries,
                candidate.Item,
                rule,
                CleanupAuditStage.SeriesPolicy,
                CleanupAuditOutcome.Matched,
                "matched series policy because every series episode matched");
            yield return candidate;
        }
    }
}
