using System;
using System.Collections.Generic;
using System.Linq;

namespace MediaCleaner.Core;

internal static class SeriesPolicyEvaluator
{
    public static IEnumerable<CandidateItem> Apply(
        IEnumerable<CandidateItem> candidates,
        CleanupRule rule,
        List<CleanupAuditEntry> auditEntries)
    {
        var items = candidates.ToList();
        foreach (var item in items.Where(x => x.Item.Kind != MediaItemKind.Episode))
        {
            yield return item;
        }

        var episodes = items.Where(x => x.Item.Kind == MediaItemKind.Episode).ToList();
        if (episodes.Count == 0)
        {
            yield break;
        }

        var seriesItems = rule.Filters.DeleteEpisodes switch
        {
            SeriesDeleteKind.Episode => KeepEpisodes(episodes, rule, auditEntries),
            SeriesDeleteKind.Season => BuildSeasonCandidates(episodes, rule, auditEntries),
            SeriesDeleteKind.Series => BuildSeriesCandidates(episodes, rule, auditEntries, requireEnded: false),
            SeriesDeleteKind.SeriesEnded => BuildSeriesCandidates(episodes, rule, auditEntries, requireEnded: true),
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
        foreach (var item in items)
        {
            if (rule.Filters.KeepSeriesKind == SeriesKeepKind.First && item.Item.Id == item.Item.FirstEpisodeId)
            {
                CleanupAudit.AddItem(
                    auditEntries,
                    item.Item,
                    rule,
                    CleanupAuditStage.SeriesPolicy,
                    CleanupAuditOutcome.Rejected,
                    "rejected by series policy because first episode is kept");
                continue;
            }

            if (rule.Filters.KeepSeriesKind == SeriesKeepKind.Last && item.Item.Id == item.Item.LastEpisodeId)
            {
                CleanupAudit.AddItem(
                    auditEntries,
                    item.Item,
                    rule,
                    CleanupAuditStage.SeriesPolicy,
                    CleanupAuditOutcome.Rejected,
                    "rejected by series policy because last episode is kept");
                continue;
            }

            if (rule.Filters.KeepSeriesKind is not SeriesKeepKind.None and not SeriesKeepKind.First and not SeriesKeepKind.Last)
            {
                throw new NotSupportedException($"Unsupported series keep kind: {rule.Filters.KeepSeriesKind}");
            }

            yield return item;
        }
    }

    private static IEnumerable<CandidateItem> BuildSeasonCandidates(
        IEnumerable<CandidateItem> items,
        CleanupRule rule,
        List<CleanupAuditEntry> auditEntries)
    {
        foreach (var group in items.GroupBy(x => x.Item.SeasonId ?? x.Item.SeriesId ?? x.Item.Id))
        {
            var first = group.MaxBy(x => x.Playback.FirstOrDefault()?.LastPlayedDate ?? x.Item.DateCreated);
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

            var seasonEpisodes = first.Item.SeasonEpisodeIds ?? first.Item.EpisodeIds ?? [];
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

            if (rule.Filters.KeepSeriesKind == SeriesKeepKind.First && first.Item.SeasonId == first.Item.FirstSeasonId)
            {
                CleanupAudit.AddItem(
                    auditEntries,
                    first.Item,
                    rule,
                    CleanupAuditStage.SeriesPolicy,
                    CleanupAuditOutcome.Rejected,
                    "rejected by series policy because first season is kept");
                continue;
            }

            if (rule.Filters.KeepSeriesKind == SeriesKeepKind.Last && first.Item.SeasonId == first.Item.LastSeasonId)
            {
                CleanupAudit.AddItem(
                    auditEntries,
                    first.Item,
                    rule,
                    CleanupAuditStage.SeriesPolicy,
                    CleanupAuditOutcome.Rejected,
                    "rejected by series policy because last season is kept");
                continue;
            }

            if (rule.Filters.KeepSeriesKind is not SeriesKeepKind.None and not SeriesKeepKind.First and not SeriesKeepKind.Last)
            {
                throw new NotSupportedException($"Unsupported series keep kind: {rule.Filters.KeepSeriesKind}");
            }

            var candidate = new CandidateItem(
                first.Item with
                {
                    Id = first.Item.SeasonId,
                    Kind = MediaItemKind.Season,
                    Name = first.Item.SeasonName ?? first.Item.Name,
                    FullName = $"{first.Item.SeriesName} | S{first.Item.ParentIndexNumber:D2} | {first.Item.SeasonName ?? first.Item.Name}",
                    IndexNumber = first.Item.ParentIndexNumber,
                    EpisodeIds = first.Item.SeasonEpisodeIds,
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
        bool requireEnded)
    {
        foreach (var group in items.GroupBy(x => x.Item.SeriesId ?? x.Item.Id))
        {
            var first = group.MaxBy(x => x.Playback.FirstOrDefault()?.LastPlayedDate ?? x.Item.DateCreated);
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

            var seriesEpisodes = first.Item.SeriesEpisodeIds ?? first.Item.EpisodeIds ?? [];
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

            var candidate = new CandidateItem(
                first.Item with
                {
                    Id = first.Item.SeriesId,
                    Kind = MediaItemKind.Series,
                    Name = first.Item.SeriesName ?? first.Item.Name,
                    FullName = first.Item.SeriesName ?? first.Item.Name,
                    EpisodeIds = first.Item.SeriesEpisodeIds,
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
