using System;
using System.Collections.Generic;
using System.Linq;

namespace MediaCleaner.Core;

internal sealed class CleanupRuleMatcher(DateTime now, IPathMatcher pathMatcher, CleanupPolicy policy)
{
    internal int TriggerEvaluationCount { get; private set; }

    public RuleEvaluationContext? CreateContext(
        IReadOnlyList<MediaUser> requestUsers,
        CleanupRule rule,
        CleanupAuditCollector audit)
    {
        if (rule.Trigger.Days < 0)
        {
            CleanupAudit.AddRule(
                audit,
                rule,
                CleanupAuditStage.RuleEligibility,
                CleanupAuditOutcome.Skipped,
                $"rule '{rule.Name}' skipped because days is negative");

            return null;
        }

        var users = CleanupPlanner.FilterUsers(requestUsers, rule.Filters.UserIds, rule.Filters.UsersMode).ToList();
        if (rule.Trigger.Kind is CleanupRuleTriggerKind.Played or CleanupRuleTriggerKind.NotPlayed && users.Count == 0)
        {
            CleanupAudit.AddRule(
                audit,
                rule,
                CleanupAuditStage.RuleEligibility,
                CleanupAuditOutcome.Skipped,
                $"rule '{rule.Name}' skipped because no users matched its user filter");

            return null;
        }

        var favoriteUsers = CleanupPlanner.FilterUsers(requestUsers, rule.Filters.FavoriteUserIds, rule.Filters.FavoriteUsersMode).ToList();
        var startDate = rule.Trigger.CountAsNotPlayedAfter >= 0
            ? now.AddDays(-rule.Trigger.CountAsNotPlayedAfter)
            : (DateTime?)null;
        return new RuleEvaluationContext(
            rule,
            users,
            users.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase),
            favoriteUsers,
            rule.Filters.MediaKinds.ToHashSet(),
            startDate,
            now.AddDays(-rule.Trigger.Days));
    }

    public IEnumerable<RuleMatch> CollectRuleMatches(
        CleanupCatalogIndex catalog,
        RuleEvaluationContext context,
        CleanupAuditCollector audit)
    {
        var rule = context.Rule;
        var items = catalog.GetRuleItems(context.MediaKinds);
        if (!audit.Enabled)
        {
            foreach (var match in CollectRuleMatchesFast(items, context, catalog.ItemsById, audit))
            {
                yield return match;
            }

            yield break;
        }

        var candidates = rule.Trigger.Kind switch
        {
            CleanupRuleTriggerKind.Played => CollectPlayed(items, context, audit),
            CleanupRuleTriggerKind.NotPlayed => CollectNotPlayed(items, context, audit),
            CleanupRuleTriggerKind.AddedAge => CollectAddedAge(items, context),
            _ => throw new NotSupportedException($"Unsupported rule trigger: {rule.Trigger.Kind}"),
        };

        var filtered = new List<CandidateItem>();
        foreach (var candidate in candidates)
        {
            CleanupAudit.AddItem(
                audit,
                candidate.Item,
                rule,
                CleanupAuditStage.Trigger,
                CleanupAuditOutcome.Matched,
                $"matched {rule.Trigger.Kind} rule '{rule.Name}'");

            if (!IsAllowedByFavorites(candidate.Item, context.FavoriteUsers, rule.Filters.FavoriteFilter))
            {
                CleanupAudit.AddItem(
                    audit,
                    candidate.Item,
                    rule,
                    CleanupAuditStage.FavoriteFilter,
                    CleanupAuditOutcome.Rejected,
                    $"rejected by favorite filter '{rule.Filters.FavoriteFilter}'");

                continue;
            }

            if (!IsAllowedByLocation(candidate.Item, rule.Filters))
            {
                CleanupAudit.AddItem(
                    audit,
                    candidate.Item,
                    rule,
                    CleanupAuditStage.LocationFilter,
                    CleanupAuditOutcome.Rejected,
                    $"rejected by location filter '{rule.Filters.LocationsMode}'");

                continue;
            }

            if (!IsAllowedByTag(candidate.Item, rule.Filters))
            {
                CleanupAudit.AddItem(
                    audit,
                    candidate.Item,
                    rule,
                    CleanupAuditStage.TagFilter,
                    CleanupAuditOutcome.Rejected,
                    $"rejected by tag filter '{rule.Filters.TagFilterMode}'");

                continue;
            }

            filtered.Add(candidate);
        }

        foreach (var item in SeriesPolicyEvaluator.Apply(filtered, rule, audit, catalog.ItemsById))
        {
            yield return new RuleMatch(rule, item.Item, CleanupRuleKinds.ToExpiredKind(rule.Trigger.Kind), item.Playback);
        }
    }

    private IEnumerable<RuleMatch> CollectRuleMatchesFast(
        IEnumerable<MediaItem> items,
        RuleEvaluationContext context,
        IReadOnlyDictionary<string, MediaItem> itemsById,
        CleanupAuditCollector audit)
    {
        var rule = context.Rule;
        var filtered = new List<CandidateItem>();
        foreach (var item in items)
        {
            if (!IsAllowedByTag(item, rule.Filters)
                || !IsAllowedByLocation(item, rule.Filters)
                || !IsAllowedByFavorites(item, context.FavoriteUsers, rule.Filters.FavoriteFilter))
            {
                continue;
            }

            TriggerEvaluationCount++;
            var candidate = rule.Trigger.Kind switch
            {
                CleanupRuleTriggerKind.Played => CollectPlayedCandidate(item, context, null),
                CleanupRuleTriggerKind.NotPlayed => CollectNotPlayedCandidate(item, context, null),
                CleanupRuleTriggerKind.AddedAge => CollectAddedAgeCandidate(item, context),
                _ => throw new NotSupportedException($"Unsupported rule trigger: {rule.Trigger.Kind}"),
            };
            if (candidate is not null)
            {
                filtered.Add(candidate);
            }
        }

        foreach (var item in SeriesPolicyEvaluator.Apply(filtered, rule, audit, itemsById))
        {
            yield return new RuleMatch(rule, item.Item, CleanupRuleKinds.ToExpiredKind(rule.Trigger.Kind), item.Playback);
        }
    }

    private IEnumerable<CandidateItem> CollectPlayed(
        IEnumerable<MediaItem> items,
        RuleEvaluationContext context,
        CleanupAuditCollector audit)
    {
        var rule = context.Rule;

        foreach (var item in items)
        {
            if (CollectPlayedCandidate(item, context, audit) is { } candidate)
            {
                yield return candidate;
            }
        }
    }

    private CandidateItem? CollectPlayedCandidate(
        MediaItem item,
        RuleEvaluationContext context,
        CleanupAuditCollector? audit)
    {
        var playback = new List<PlaybackState>();
        foreach (var state in item.Playback)
        {
            if (!context.UserIds.Contains(state.UserId)
                || !state.HasUserData
                || (!state.IsPlayed && !state.IsWatching)
                || !state.LastPlayedDate.HasValue
                || (context.PlaybackStartDate is not null && state.LastPlayedDate < context.PlaybackStartDate))
            {
                continue;
            }

            if (!policy.AllowDeleteIfPlayedBeforeAdded && state.LastPlayedDate < item.DateCreated)
            {
                if (audit is not null)
                {
                    AddPlayedBeforeAddedAudit(audit, item, context.Rule, state, "ignored playback");
                }

                continue;
            }

            playback.Add(state);
        }

        StableSortPlaybackDescending(playback);
        return playback.Count > 0 && IsPlayedExpired(playback, context.Users.Count, context.Rule.Trigger, context.ExpirationCutoffDate)
            ? new CandidateItem(item, playback)
            : null;
    }

    private IEnumerable<CandidateItem> CollectNotPlayed(
        IEnumerable<MediaItem> items,
        RuleEvaluationContext context,
        CleanupAuditCollector audit)
    {
        var rule = context.Rule;

        foreach (var item in items)
        {
            if (CollectNotPlayedCandidate(item, context, audit) is { } candidate)
            {
                yield return candidate;
            }
        }
    }

    private CandidateItem? CollectNotPlayedCandidate(
        MediaItem item,
        RuleEvaluationContext context,
        CleanupAuditCollector? audit)
    {
        var notPlayed = new List<PlaybackState>();
        var hasPlayedBeforeAdded = false;
        foreach (var state in item.Playback)
        {
            if (!context.UserIds.Contains(state.UserId) || !state.HasUserData)
            {
                continue;
            }

            var isPlayedBeforeAdded = !policy.AllowDeleteIfPlayedBeforeAdded
                && state.IsPlayed
                && state.LastPlayedDate.HasValue
                && state.LastPlayedDate < item.DateCreated
                && (context.PlaybackStartDate is null || state.LastPlayedDate >= context.PlaybackStartDate);
            if (isPlayedBeforeAdded)
            {
                hasPlayedBeforeAdded = true;
                if (audit is not null)
                {
                    AddPlayedBeforeAddedAudit(audit, item, context.Rule, state, "blocked not-played match");
                }
            }

            var isPlayedAfterCreated = policy.AllowDeleteIfPlayedBeforeAdded || state.LastPlayedDate >= item.DateCreated;
            var shouldSkip = (state.IsPlayed && isPlayedAfterCreated) || state.IsWatching;
            if (context.PlaybackStartDate is null ? !shouldSkip : !(shouldSkip && state.LastPlayedDate >= context.PlaybackStartDate))
            {
                notPlayed.Add(state);
            }
        }

        return !hasPlayedBeforeAdded
            && notPlayed.Count == context.Users.Count
            && item.DateCreated <= context.ExpirationCutoffDate
                ? new CandidateItem(item, notPlayed)
                : null;
    }

    private IEnumerable<CandidateItem> CollectAddedAge(
        IEnumerable<MediaItem> items,
        RuleEvaluationContext context)
    {
        return items
            .Where(x => x.DateCreated <= context.ExpirationCutoffDate)
            .Select(item => new CandidateItem(
                item,
                item.Playback.Where(x => context.UserIds.Count == 0 || context.UserIds.Contains(x.UserId)).ToList()));
    }

    private CandidateItem? CollectAddedAgeCandidate(MediaItem item, RuleEvaluationContext context)
    {
        if (item.DateCreated > context.ExpirationCutoffDate)
        {
            return null;
        }

        var playback = new List<PlaybackState>();
        foreach (var state in item.Playback)
        {
            if (context.UserIds.Count == 0 || context.UserIds.Contains(state.UserId))
            {
                playback.Add(state);
            }
        }

        return new CandidateItem(item, playback);
    }

    private static bool IsPlayedExpired(
        IReadOnlyList<PlaybackState> playback,
        int usersCount,
        CleanupRuleTrigger trigger,
        DateTime expirationCutoffDate)
    {
        return trigger.PlayedKeepKind switch
        {
            PlayedKeepKind.AnyUser => playback.Any(x => x.IsPlayed && x.LastPlayedDate <= expirationCutoffDate),
            PlayedKeepKind.AnyUserRolling => IsAnyUserRollingExpired(playback, expirationCutoffDate),
            PlayedKeepKind.AllUsers => playback.Count(x => x.IsPlayed && x.LastPlayedDate <= expirationCutoffDate) >= usersCount,
            _ => throw new NotSupportedException($"Unsupported played keep kind: {trigger.PlayedKeepKind}"),
        };
    }

    private static bool IsAnyUserRollingExpired(
        IReadOnlyList<PlaybackState> playback,
        DateTime expirationCutoffDate)
    {
        foreach (var state in playback)
        {
            if (state.IsWatching)
            {
                return false;
            }
        }

        // Candidate playback is sorted newest-first and contains only played or watching states.
        // With watching states excluded above, the first state is the latest played state.
        return playback[0].LastPlayedDate <= expirationCutoffDate;
    }

    private static void StableSortPlaybackDescending(List<PlaybackState> playback)
    {
        for (var index = 1; index < playback.Count; index++)
        {
            var current = playback[index];
            var insertionIndex = index;
            while (insertionIndex > 0
                && current.LastPlayedDate > playback[insertionIndex - 1].LastPlayedDate)
            {
                playback[insertionIndex] = playback[insertionIndex - 1];
                insertionIndex--;
            }

            playback[insertionIndex] = current;
        }
    }

    private static void AddPlayedBeforeAddedAudit(
        CleanupAuditCollector audit,
        MediaItem item,
        CleanupRule rule,
        PlaybackState playback,
        string action)
    {
        var user = string.IsNullOrWhiteSpace(playback.UserName) ? playback.UserId : playback.UserName;
        CleanupAudit.AddItem(
            audit,
            item,
            rule,
            CleanupAuditStage.Trigger,
            CleanupAuditOutcome.Skipped,
            $"{action} for user '{user}' because Last Played ({playback.LastPlayedDate!.Value.ToLocalTime()}) is before Date Added ({item.DateCreated.ToLocalTime()}); this usually happens after a file upgrade or re-import");
    }

    private static bool IsAllowedByFavorites(MediaItem item, IReadOnlyList<MediaUser> users, RuleFavoriteFilterKind filter)
    {
        return filter switch
        {
            RuleFavoriteFilterKind.Ignore => true,
            RuleFavoriteFilterKind.FavoriteByAnyUser => users.Any(user => IsFavoriteForUser(item, user.Id)),
            RuleFavoriteFilterKind.FavoriteByAllUsers => users.Count > 0 && users.All(user => IsFavoriteForUser(item, user.Id)),
            RuleFavoriteFilterKind.NotFavoriteByAnyUser => !users.Any(user => IsFavoriteForUser(item, user.Id)),
            RuleFavoriteFilterKind.NotFavoriteByAllUsers => !users.All(user => IsFavoriteForUser(item, user.Id)),
            _ => throw new NotSupportedException($"Unsupported favorite filter kind: {filter}"),
        };
    }

    private bool IsAllowedByLocation(MediaItem item, CleanupRuleFilters filters)
    {
        if (filters.Locations.Count == 0)
        {
            return true;
        }

        var path = item.LocationPath ?? item.Path;
        if (string.IsNullOrEmpty(path))
        {
            return filters.LocationsMode == LocationsListMode.Exclude;
        }

        var contains = filters.Locations.Any(location => pathMatcher.ContainsSubPath(location, path));
        return filters.LocationsMode switch
        {
            LocationsListMode.Exclude => !contains,
            LocationsListMode.Include => contains,
            _ => throw new NotSupportedException($"Unsupported locations mode: {filters.LocationsMode}"),
        };
    }

    private static bool IsAllowedByTag(MediaItem item, CleanupRuleFilters filters)
    {
        if (!filters.EnableTagFilter)
        {
            return true;
        }

        if (filters.Tags.Count == 0)
        {
            return filters.TagFilterMode switch
            {
                TagMode.Exclusion => true,
                TagMode.Inclusion => false,
                _ => throw new NotSupportedException($"Unsupported tag mode: {filters.TagFilterMode}"),
            };
        }

        var hasTag = filters.Tags.Any(item.Tags.Contains);
        return filters.TagFilterMode switch
        {
            TagMode.Exclusion => !hasTag,
            TagMode.Inclusion => hasTag,
            _ => throw new NotSupportedException($"Unsupported tag mode: {filters.TagFilterMode}"),
        };
    }

    private static bool IsFavoriteForUser(MediaItem item, string userId) =>
        item.Playback.Any(x => string.Equals(x.UserId, userId, StringComparison.OrdinalIgnoreCase) && x.IsFavorite);
}
