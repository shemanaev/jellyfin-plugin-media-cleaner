using System;
using System.Collections.Generic;
using System.Linq;

namespace MediaCleaner.Core;

internal sealed class CleanupRuleMatcher(DateTime now, IPathMatcher pathMatcher, CleanupPolicy policy)
{
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
            startDate);
    }

    public IEnumerable<RuleMatch> CollectRuleMatches(
        CleanupCatalogIndex catalog,
        RuleEvaluationContext context,
        CleanupAuditCollector audit)
    {
        var rule = context.Rule;
        var items = catalog.GetRuleItems(context.MediaKinds);
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

    private IEnumerable<CandidateItem> CollectPlayed(
        IEnumerable<MediaItem> items,
        RuleEvaluationContext context,
        CleanupAuditCollector audit)
    {
        var rule = context.Rule;

        foreach (var item in items)
        {
            var playback = item.Playback
                .Where(x => context.UserIds.Contains(x.UserId))
                .Where(x => x.HasUserData)
                .Where(x => x.IsPlayed || x.IsWatching)
                .Where(x => x.LastPlayedDate.HasValue)
                .Where(x => context.PlaybackStartDate is null || x.LastPlayedDate >= context.PlaybackStartDate)
                .Where(x =>
                {
                    if (policy.AllowDeleteIfPlayedBeforeAdded || x.LastPlayedDate >= item.DateCreated)
                    {
                        return true;
                    }

                    AddPlayedBeforeAddedAudit(audit, item, rule, x, "ignored playback");
                    return false;
                })
                .OrderByDescending(x => x.LastPlayedDate)
                .ToList();
            var candidate = new CandidateItem(item, playback);
            if (candidate.Playback.Count > 0 && IsPlayedExpired(candidate.Playback, context.Users.Count, rule.Trigger))
            {
                yield return candidate;
            }
        }
    }

    private IEnumerable<CandidateItem> CollectNotPlayed(
        IEnumerable<MediaItem> items,
        RuleEvaluationContext context,
        CleanupAuditCollector audit)
    {
        var rule = context.Rule;

        foreach (var item in items)
        {
            var hasPlayedBeforeAdded = false;
            var notPlayed = item.Playback
                .Where(x => context.UserIds.Contains(x.UserId))
                .Where(x => x.HasUserData)
                .Where(x =>
                {
                    var isPlayedBeforeAdded = !policy.AllowDeleteIfPlayedBeforeAdded
                        && x.IsPlayed
                        && x.LastPlayedDate.HasValue
                        && x.LastPlayedDate < item.DateCreated
                        && (context.PlaybackStartDate is null || x.LastPlayedDate >= context.PlaybackStartDate);
                    if (isPlayedBeforeAdded)
                    {
                        hasPlayedBeforeAdded = true;
                        AddPlayedBeforeAddedAudit(audit, item, rule, x, "blocked not-played match");
                    }

                    var isPlayedAfterCreated = policy.AllowDeleteIfPlayedBeforeAdded || x.LastPlayedDate >= item.DateCreated;
                    var shouldSkip = (x.IsPlayed && isPlayedAfterCreated) || x.IsWatching;
                    return context.PlaybackStartDate is null ? !shouldSkip : !(shouldSkip && x.LastPlayedDate >= context.PlaybackStartDate);
                })
                .ToList();
            if (hasPlayedBeforeAdded)
            {
                continue;
            }

            var candidate = new CandidateItem(item, notPlayed);
            if (candidate.Playback.Count == context.Users.Count && now >= candidate.Item.DateCreated.AddDays(rule.Trigger.Days))
            {
                yield return candidate;
            }
        }
    }

    private IEnumerable<CandidateItem> CollectAddedAge(
        IEnumerable<MediaItem> items,
        RuleEvaluationContext context)
    {
        var rule = context.Rule;
        return items
            .Where(x => now >= x.DateCreated.AddDays(rule.Trigger.Days))
            .Select(item => new CandidateItem(
                item,
                item.Playback.Where(x => context.UserIds.Count == 0 || context.UserIds.Contains(x.UserId)).ToList()));
    }

    private bool IsPlayedExpired(IReadOnlyList<PlaybackState> playback, int usersCount, CleanupRuleTrigger trigger)
    {
        return trigger.PlayedKeepKind switch
        {
            PlayedKeepKind.AnyUser => playback.Any(x => x.IsPlayed && now >= x.LastPlayedDate!.Value.AddDays(trigger.Days)),
            PlayedKeepKind.AnyUserRolling => !playback.Any(x => x.IsWatching)
                && playback.Where(x => x.IsPlayed).OrderByDescending(x => x.LastPlayedDate).FirstOrDefault() is { } latest
                && now >= latest.LastPlayedDate!.Value.AddDays(trigger.Days),
            PlayedKeepKind.AllUsers => playback.Count(x => x.IsPlayed && now >= x.LastPlayedDate!.Value.AddDays(trigger.Days)) >= usersCount,
            _ => throw new NotSupportedException($"Unsupported played keep kind: {trigger.PlayedKeepKind}"),
        };
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
