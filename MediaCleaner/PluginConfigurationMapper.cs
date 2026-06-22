using System;
using System.Collections.Generic;
using System.Linq;
using MediaCleaner.Configuration;
using CoreCleanupPolicy = MediaCleaner.Core.CleanupPolicy;
using CoreCleanupRule = MediaCleaner.Core.CleanupRule;
using CoreCleanupRuleActions = MediaCleaner.Core.CleanupRuleActions;
using CoreCleanupRuleActionKind = MediaCleaner.Core.CleanupRuleActionKind;
using CoreCleanupRuleFilters = MediaCleaner.Core.CleanupRuleFilters;
using CoreCleanupRuleTrigger = MediaCleaner.Core.CleanupRuleTrigger;
using CoreCleanupRuleTriggerKind = MediaCleaner.Core.CleanupRuleTriggerKind;
using CoreLocationsListMode = MediaCleaner.Core.LocationsListMode;
using CoreMediaItemKind = MediaCleaner.Core.MediaItemKind;
using CorePlayedKeepKind = MediaCleaner.Core.PlayedKeepKind;
using CoreRuleFavoriteFilterKind = MediaCleaner.Core.RuleFavoriteFilterKind;
using CoreSeriesDeleteKind = MediaCleaner.Core.SeriesDeleteKind;
using CoreSeriesKeepKind = MediaCleaner.Core.SeriesKeepKind;
using CoreTagMode = MediaCleaner.Core.TagMode;
using CoreUsersListMode = MediaCleaner.Core.UsersListMode;
using ConfigCleanupRuleActionKind = MediaCleaner.Configuration.CleanupRuleActionKind;
using ConfigCleanupRuleTriggerKind = MediaCleaner.Configuration.CleanupRuleTriggerKind;
using ConfigFavoriteKeepKind = MediaCleaner.Configuration.FavoriteKeepKind;
using ConfigLocationsListMode = MediaCleaner.Configuration.LocationsListMode;
using ConfigPlayedKeepKind = MediaCleaner.Configuration.PlayedKeepKind;
using ConfigRuleFavoriteFilterKind = MediaCleaner.Configuration.RuleFavoriteFilterKind;
using ConfigRuleMediaKind = MediaCleaner.Configuration.RuleMediaKind;
using ConfigSeriesDeleteKind = MediaCleaner.Configuration.SeriesDeleteKind;
using ConfigSeriesKeepKind = MediaCleaner.Configuration.SeriesKeepKind;
using ConfigTagMode = MediaCleaner.Configuration.TagMode;
using ConfigUsersListMode = MediaCleaner.Configuration.UsersListMode;

namespace MediaCleaner;

internal static class PluginConfigurationMapper
{
    private const int CurrentConfigVersion = 2;

    public static CoreCleanupPolicy ToCleanupPolicy(this PluginConfiguration config)
    {
        return new CoreCleanupPolicy(
            Rules: GetEffectiveRules(config).Select(Map).ToList(),
            AllowDeleteIfPlayedBeforeAdded: config.AllowDeleteIfPlayedBeforeAdded);
    }

    public static bool RequiresMigrationReview(this PluginConfiguration config) =>
        config.ConfigVersion < CurrentConfigVersion && GetEffectiveRules(config).Count > 0;

    public static void EnsureRulesMigrated(PluginConfiguration config)
    {
        if (config.ConfigVersion >= CurrentConfigVersion)
        {
            return;
        }

        config.Rules = GetEffectiveRules(config).ToList();
        config.ConfigVersion = CurrentConfigVersion;
    }

    private static IReadOnlyList<CleanupRuleConfiguration> GetEffectiveRules(PluginConfiguration config)
    {
        if (config.ConfigVersion >= CurrentConfigVersion || config.Rules.Count > 0)
        {
            return config.Rules;
        }

        return BuildLegacyRules(config).ToList();
    }

    private static IEnumerable<CleanupRuleConfiguration> BuildLegacyRules(PluginConfiguration config)
    {
        foreach (var rule in BuildLegacyRulesForKind(
            config,
            "Movies",
            ConfigRuleMediaKind.Movie,
            config.KeepMoviesFor,
            config.KeepMoviesNotPlayedFor,
            config.KeepPlayedMovies,
            config.KeepFavoriteMovies,
            ConfigSeriesDeleteKind.Episode,
            ConfigSeriesKeepKind.None))
        {
            yield return rule;
        }

        foreach (var rule in BuildLegacyRulesForKind(
            config,
            "Episodes",
            ConfigRuleMediaKind.Episode,
            config.KeepEpisodesFor,
            config.KeepEpisodesNotPlayedFor,
            config.KeepPlayedEpisodes,
            config.KeepFavoriteEpisodes,
            config.DeleteEpisodes,
            config.KeepSeriesKind))
        {
            yield return rule;
        }

        foreach (var rule in BuildLegacyRulesForKind(
            config,
            "Videos",
            ConfigRuleMediaKind.Video,
            config.KeepVideosFor,
            config.KeepVideosNotPlayedFor,
            config.KeepPlayedVideos,
            config.KeepFavoriteVideos,
            ConfigSeriesDeleteKind.Episode,
            ConfigSeriesKeepKind.None))
        {
            yield return rule;
        }

        foreach (var rule in BuildLegacyRulesForKind(
            config,
            "Audio",
            ConfigRuleMediaKind.Audio,
            config.KeepAudioFor,
            config.KeepAudioNotPlayedFor,
            config.KeepPlayedAudio,
            config.KeepFavoriteAudio,
            ConfigSeriesDeleteKind.Episode,
            ConfigSeriesKeepKind.None))
        {
            yield return rule;
        }

        foreach (var rule in BuildLegacyRulesForKind(
            config,
            "Audio books",
            ConfigRuleMediaKind.AudioBook,
            config.KeepAudioBooksFor,
            config.KeepAudioBooksNotPlayedFor,
            config.KeepPlayedAudioBooks,
            config.KeepFavoriteAudioBooks,
            ConfigSeriesDeleteKind.Episode,
            ConfigSeriesKeepKind.None))
        {
            yield return rule;
        }
    }

    private static IEnumerable<CleanupRuleConfiguration> BuildLegacyRulesForKind(
        PluginConfiguration config,
        string name,
        ConfigRuleMediaKind mediaKind,
        int keepPlayedFor,
        int keepNotPlayedFor,
        ConfigPlayedKeepKind playedKeepKind,
        ConfigFavoriteKeepKind favoriteKeepKind,
        ConfigSeriesDeleteKind deleteEpisodes,
        ConfigSeriesKeepKind keepSeriesKind)
    {
        if (keepPlayedFor >= 0)
        {
            yield return CreateLegacyRule(
                config,
                $"legacy-{mediaKind.ToString().ToLowerInvariant()}-played",
                $"{name} played",
                mediaKind,
                ConfigCleanupRuleTriggerKind.Played,
                keepPlayedFor,
                playedKeepKind,
                favoriteKeepKind,
                deleteEpisodes,
                keepSeriesKind);
        }

        if (keepNotPlayedFor >= 0)
        {
            yield return CreateLegacyRule(
                config,
                $"legacy-{mediaKind.ToString().ToLowerInvariant()}-not-played",
                $"{name} not played",
                mediaKind,
                ConfigCleanupRuleTriggerKind.NotPlayed,
                keepNotPlayedFor,
                playedKeepKind,
                favoriteKeepKind,
                deleteEpisodes,
                keepSeriesKind);
        }
    }

    private static CleanupRuleConfiguration CreateLegacyRule(
        PluginConfiguration config,
        string id,
        string name,
        ConfigRuleMediaKind mediaKind,
        ConfigCleanupRuleTriggerKind triggerKind,
        int days,
        ConfigPlayedKeepKind playedKeepKind,
        ConfigFavoriteKeepKind favoriteKeepKind,
        ConfigSeriesDeleteKind deleteEpisodes,
        ConfigSeriesKeepKind keepSeriesKind)
    {
        var tag = config.TagFilterMode == ConfigTagMode.Exclusion ? config.ExclusionTag : config.InclusionTag;
        return new CleanupRuleConfiguration
        {
            Id = id,
            Name = name,
            Enabled = true,
            Trigger = new CleanupRuleTriggerConfiguration
            {
                Kind = triggerKind,
                Days = days,
                PlayedKeepKind = playedKeepKind,
                CountAsNotPlayedAfter = config.CountAsNotPlayedAfter,
            },
            Filters = new CleanupRuleFiltersConfiguration
            {
                MediaKinds = new List<ConfigRuleMediaKind> { mediaKind },
                UserIds = new List<string>(config.UsersIgnorePlayed),
                UsersMode = config.UsersPlayedMode,
                FavoriteUserIds = new List<string>(config.UsersIgnoreFavorited),
                FavoriteUsersMode = config.UsersFavoritedMode,
                FavoriteFilter = MapFavoriteFilter(favoriteKeepKind),
                Locations = new List<string>(config.LocationsExcluded),
                LocationsMode = config.LocationsMode,
                EnableTagFilter = config.EnableTagExclusion,
                TagFilterMode = config.TagFilterMode,
                Tags = string.IsNullOrWhiteSpace(tag) ? new List<string>() : new List<string> { tag },
                DeleteEpisodes = deleteEpisodes,
                KeepSeriesKind = keepSeriesKind,
            },
            Actions = new CleanupRuleActionsConfiguration
            {
                Kind = ConfigCleanupRuleActionKind.Delete,
                MarkAsUnplayed = config.MarkAsUnplayed,
            },
        };
    }

    private static ConfigRuleFavoriteFilterKind MapFavoriteFilter(ConfigFavoriteKeepKind value) => value switch
    {
        ConfigFavoriteKeepKind.DontKeep => ConfigRuleFavoriteFilterKind.Ignore,
        ConfigFavoriteKeepKind.AnyUser => ConfigRuleFavoriteFilterKind.NotFavoriteByAnyUser,
        ConfigFavoriteKeepKind.AllUsers => ConfigRuleFavoriteFilterKind.NotFavoriteByAllUsers,
        _ => throw new NotSupportedException($"Unsupported favorite keep kind: {value}"),
    };

    private static CoreCleanupRule Map(CleanupRuleConfiguration value) => new(
        Id: string.IsNullOrWhiteSpace(value.Id) ? Guid.NewGuid().ToString("N") : value.Id,
        Name: string.IsNullOrWhiteSpace(value.Name) ? "Unnamed rule" : value.Name,
        Enabled: value.Enabled,
        Trigger: new CoreCleanupRuleTrigger(
            Map(value.Trigger.Kind),
            value.Trigger.Days,
            Map(value.Trigger.PlayedKeepKind),
            value.Trigger.CountAsNotPlayedAfter),
        Filters: new CoreCleanupRuleFilters(
            MediaKinds: value.Filters.MediaKinds.Select(Map).ToList(),
            UserIds: new List<string>(value.Filters.UserIds),
            UsersMode: Map(value.Filters.UsersMode),
            FavoriteUserIds: new List<string>(value.Filters.FavoriteUserIds),
            FavoriteUsersMode: Map(value.Filters.FavoriteUsersMode),
            FavoriteFilter: Map(value.Filters.FavoriteFilter),
            Locations: new List<string>(value.Filters.Locations),
            LocationsMode: Map(value.Filters.LocationsMode),
            EnableTagFilter: value.Filters.EnableTagFilter,
            TagFilterMode: Map(value.Filters.TagFilterMode),
            Tags: new List<string>(value.Filters.Tags),
            DeleteEpisodes: Map(value.Filters.DeleteEpisodes),
            KeepSeriesKind: Map(value.Filters.KeepSeriesKind)),
        Actions: new CoreCleanupRuleActions(Map(value.Actions.Kind), value.Actions.MarkAsUnplayed));

    private static CoreCleanupRuleTriggerKind Map(ConfigCleanupRuleTriggerKind value) => value switch
    {
        ConfigCleanupRuleTriggerKind.Played => CoreCleanupRuleTriggerKind.Played,
        ConfigCleanupRuleTriggerKind.NotPlayed => CoreCleanupRuleTriggerKind.NotPlayed,
        ConfigCleanupRuleTriggerKind.AddedAge => CoreCleanupRuleTriggerKind.AddedAge,
        _ => throw new NotSupportedException($"Unsupported rule trigger: {value}"),
    };

    private static CoreCleanupRuleActionKind Map(ConfigCleanupRuleActionKind value) => value switch
    {
        ConfigCleanupRuleActionKind.Delete => CoreCleanupRuleActionKind.Delete,
        ConfigCleanupRuleActionKind.Protect => CoreCleanupRuleActionKind.Protect,
        _ => throw new NotSupportedException($"Unsupported rule action: {value}"),
    };

    private static CoreMediaItemKind Map(ConfigRuleMediaKind value) => value switch
    {
        ConfigRuleMediaKind.Movie => CoreMediaItemKind.Movie,
        ConfigRuleMediaKind.Series => CoreMediaItemKind.Series,
        ConfigRuleMediaKind.Season => CoreMediaItemKind.Season,
        ConfigRuleMediaKind.Episode => CoreMediaItemKind.Episode,
        ConfigRuleMediaKind.Video => CoreMediaItemKind.Video,
        ConfigRuleMediaKind.Audio => CoreMediaItemKind.Audio,
        ConfigRuleMediaKind.AudioBook => CoreMediaItemKind.AudioBook,
        ConfigRuleMediaKind.Other => CoreMediaItemKind.Other,
        _ => throw new NotSupportedException($"Unsupported media kind: {value}"),
    };

    private static CorePlayedKeepKind Map(ConfigPlayedKeepKind value) => value switch
    {
        ConfigPlayedKeepKind.AnyUser => CorePlayedKeepKind.AnyUser,
        ConfigPlayedKeepKind.AnyUserRolling => CorePlayedKeepKind.AnyUserRolling,
        ConfigPlayedKeepKind.AllUsers => CorePlayedKeepKind.AllUsers,
        _ => throw new NotSupportedException($"Unsupported played keep kind: {value}"),
    };

    private static CoreRuleFavoriteFilterKind Map(ConfigRuleFavoriteFilterKind value) => value switch
    {
        ConfigRuleFavoriteFilterKind.Ignore => CoreRuleFavoriteFilterKind.Ignore,
        ConfigRuleFavoriteFilterKind.FavoriteByAnyUser => CoreRuleFavoriteFilterKind.FavoriteByAnyUser,
        ConfigRuleFavoriteFilterKind.FavoriteByAllUsers => CoreRuleFavoriteFilterKind.FavoriteByAllUsers,
        ConfigRuleFavoriteFilterKind.NotFavoriteByAnyUser => CoreRuleFavoriteFilterKind.NotFavoriteByAnyUser,
        ConfigRuleFavoriteFilterKind.NotFavoriteByAllUsers => CoreRuleFavoriteFilterKind.NotFavoriteByAllUsers,
        _ => throw new NotSupportedException($"Unsupported favorite filter kind: {value}"),
    };

    private static CoreUsersListMode Map(ConfigUsersListMode value) => value switch
    {
        ConfigUsersListMode.Ignore => CoreUsersListMode.Ignore,
        ConfigUsersListMode.Acknowledge => CoreUsersListMode.Acknowledge,
        _ => throw new NotSupportedException($"Unsupported users list mode: {value}"),
    };

    private static CoreLocationsListMode Map(ConfigLocationsListMode value) => value switch
    {
        ConfigLocationsListMode.Exclude => CoreLocationsListMode.Exclude,
        ConfigLocationsListMode.Include => CoreLocationsListMode.Include,
        _ => throw new NotSupportedException($"Unsupported locations mode: {value}"),
    };

    private static CoreTagMode Map(ConfigTagMode value) => value switch
    {
        ConfigTagMode.Exclusion => CoreTagMode.Exclusion,
        ConfigTagMode.Inclusion => CoreTagMode.Inclusion,
        _ => throw new NotSupportedException($"Unsupported tag mode: {value}"),
    };

    private static CoreSeriesDeleteKind Map(ConfigSeriesDeleteKind value) => value switch
    {
        ConfigSeriesDeleteKind.Episode => CoreSeriesDeleteKind.Episode,
        ConfigSeriesDeleteKind.Season => CoreSeriesDeleteKind.Season,
        ConfigSeriesDeleteKind.Series => CoreSeriesDeleteKind.Series,
        ConfigSeriesDeleteKind.SeriesEnded => CoreSeriesDeleteKind.SeriesEnded,
        _ => throw new NotSupportedException($"Unsupported series delete kind: {value}"),
    };

    private static CoreSeriesKeepKind Map(ConfigSeriesKeepKind value) => value switch
    {
        ConfigSeriesKeepKind.None => CoreSeriesKeepKind.None,
        ConfigSeriesKeepKind.First => CoreSeriesKeepKind.First,
        ConfigSeriesKeepKind.Last => CoreSeriesKeepKind.Last,
        _ => throw new NotSupportedException($"Unsupported series keep kind: {value}"),
    };
}
