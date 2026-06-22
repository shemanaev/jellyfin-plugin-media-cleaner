using FluentAssertions;
using MediaCleaner.Configuration;
using ConfigFavoriteKeepKind = MediaCleaner.Configuration.FavoriteKeepKind;
using ConfigPlayedKeepKind = MediaCleaner.Configuration.PlayedKeepKind;
using ConfigRuleActionKind = MediaCleaner.Configuration.CleanupRuleActionKind;
using ConfigRuleFavoriteFilterKind = MediaCleaner.Configuration.RuleFavoriteFilterKind;
using ConfigRuleMediaKind = MediaCleaner.Configuration.RuleMediaKind;
using ConfigRuleTriggerKind = MediaCleaner.Configuration.CleanupRuleTriggerKind;
using ConfigSeriesDeleteKind = MediaCleaner.Configuration.SeriesDeleteKind;
using ConfigSeriesKeepKind = MediaCleaner.Configuration.SeriesKeepKind;
using ConfigTagMode = MediaCleaner.Configuration.TagMode;
using ConfigUsersListMode = MediaCleaner.Configuration.UsersListMode;

namespace MediaCleaner.Tests;

public class PluginConfigurationMapperTests
{
    [Fact]
    public void ToCleanupPolicy_MigratesLegacyConfigurationToEquivalentRules()
    {
        var config = new PluginConfiguration
        {
            ConfigVersion = 1,
            KeepMoviesFor = 1,
            KeepMoviesNotPlayedFor = 2,
            KeepPlayedMovies = ConfigPlayedKeepKind.AllUsers,
            KeepFavoriteMovies = ConfigFavoriteKeepKind.AnyUser,
            UsersIgnorePlayed = ["abc"],
            UsersPlayedMode = ConfigUsersListMode.Acknowledge,
            UsersIgnoreFavorited = ["fav"],
            LocationsExcluded = ["/media"],
            LocationsMode = LocationsListMode.Include,
            EnableTagExclusion = true,
            TagFilterMode = ConfigTagMode.Inclusion,
            InclusionTag = "delete",
            DeleteEpisodes = ConfigSeriesDeleteKind.SeriesEnded,
            KeepSeriesKind = ConfigSeriesKeepKind.Last,
            MarkAsUnplayed = true,
            CountAsNotPlayedAfter = 30,
            AllowDeleteIfPlayedBeforeAdded = true,
        };

        var policy = config.ToCleanupPolicy();

        config.ConfigVersion.Should().Be(1);
        config.RequiresMigrationReview().Should().BeTrue();
        config.Rules.Should().BeEmpty();
        policy.Rules.Should().HaveCount(2);
        policy.AllowDeleteIfPlayedBeforeAdded.Should().BeTrue();

        var played = policy.Rules.Single(x => x.Trigger.Kind == MediaCleaner.Core.CleanupRuleTriggerKind.Played);
        played.Filters.MediaKinds.Should().Equal(MediaCleaner.Core.MediaItemKind.Movie);
        played.Trigger.Days.Should().Be(1);
        played.Trigger.PlayedKeepKind.Should().Be(MediaCleaner.Core.PlayedKeepKind.AllUsers);
        played.Trigger.CountAsNotPlayedAfter.Should().Be(30);
        played.Filters.UserIds.Should().Equal("abc");
        played.Filters.UsersMode.Should().Be(MediaCleaner.Core.UsersListMode.Acknowledge);
        played.Filters.FavoriteUserIds.Should().Equal("fav");
        played.Filters.FavoriteUsersMode.Should().Be(MediaCleaner.Core.UsersListMode.Ignore);
        played.Filters.FavoriteFilter.Should().Be(MediaCleaner.Core.RuleFavoriteFilterKind.NotFavoriteByAnyUser);
        played.Filters.Locations.Should().Equal("/media");
        played.Filters.LocationsMode.Should().Be(MediaCleaner.Core.LocationsListMode.Include);
        played.Filters.TagFilterMode.Should().Be(MediaCleaner.Core.TagMode.Inclusion);
        played.Filters.Tags.Should().Equal("delete");
        played.Actions.MarkAsUnplayed.Should().BeTrue();

        var notPlayed = policy.Rules.Single(x => x.Trigger.Kind == MediaCleaner.Core.CleanupRuleTriggerKind.NotPlayed);
        notPlayed.Trigger.Days.Should().Be(2);
        notPlayed.Trigger.CountAsNotPlayedAfter.Should().Be(30);
    }

    [Fact]
    public void EnsureRulesMigrated_ConfirmsLegacyRulesOnlyOnce()
    {
        var config = new PluginConfiguration
        {
            ConfigVersion = 1,
            KeepMoviesFor = 1,
        };

        PluginConfigurationMapper.EnsureRulesMigrated(config);
        var firstIds = config.Rules.Select(x => x.Id).ToList();
        PluginConfigurationMapper.EnsureRulesMigrated(config);

        config.ConfigVersion.Should().Be(2);
        config.RequiresMigrationReview().Should().BeFalse();
        config.Rules.Select(x => x.Id).Should().Equal(firstIds);
        config.Rules.Should().HaveCount(1);
    }

    [Fact]
    public void ToCleanupPolicy_DoesNotRequireMigrationReviewForFreshDefaultConfiguration()
    {
        var config = new PluginConfiguration
        {
            ConfigVersion = 1,
        };

        var policy = config.ToCleanupPolicy();

        config.RequiresMigrationReview().Should().BeFalse();
        config.Rules.Should().BeEmpty();
        policy.Rules.Should().BeEmpty();
    }

    [Fact]
    public void ToCleanupPolicy_DoesNotRestoreLegacyRulesWhenCurrentRulesAreEmpty()
    {
        var config = new PluginConfiguration
        {
            ConfigVersion = 2,
            Rules = [],
            KeepMoviesFor = 1,
        };

        var policy = config.ToCleanupPolicy();

        policy.Rules.Should().BeEmpty();
        config.Rules.Should().BeEmpty();
    }

    [Fact]
    public void ToCleanupPolicy_PreservesRulesFromPartiallyMigratedConfiguration()
    {
        var existing = new CleanupRuleConfiguration
        {
            Id = "existing",
            Name = "existing",
            Trigger = new CleanupRuleTriggerConfiguration { Days = 5 },
            Filters = new CleanupRuleFiltersConfiguration { MediaKinds = [ConfigRuleMediaKind.Movie] },
        };
        var config = new PluginConfiguration
        {
            ConfigVersion = 1,
            Rules = [existing],
            KeepMoviesFor = 1,
        };

        var policy = config.ToCleanupPolicy();

        config.ConfigVersion.Should().Be(1);
        config.RequiresMigrationReview().Should().BeTrue();
        config.Rules.Should().ContainSingle().Which.Should().BeSameAs(existing);
        policy.Rules.Should().ContainSingle(x => x.Id == "existing");
    }

    [Fact]
    public void ToCleanupPolicy_MigratesEveryLegacyMediaKindAndTrigger()
    {
        var config = new PluginConfiguration
        {
            ConfigVersion = 1,
            KeepMoviesFor = 1,
            KeepMoviesNotPlayedFor = 2,
            KeepEpisodesFor = 3,
            KeepEpisodesNotPlayedFor = 4,
            KeepVideosFor = 5,
            KeepVideosNotPlayedFor = 6,
            KeepAudioFor = 7,
            KeepAudioNotPlayedFor = 8,
            KeepAudioBooksFor = 9,
            KeepAudioBooksNotPlayedFor = 10,
            CountAsNotPlayedAfter = 45,
            DeleteEpisodes = ConfigSeriesDeleteKind.SeriesEnded,
            KeepSeriesKind = ConfigSeriesKeepKind.Last,
        };

        var policy = config.ToCleanupPolicy();

        var expected = new Dictionary<(MediaCleaner.Core.MediaItemKind MediaKind, MediaCleaner.Core.CleanupRuleTriggerKind Trigger), int>
        {
            [(MediaCleaner.Core.MediaItemKind.Movie, MediaCleaner.Core.CleanupRuleTriggerKind.Played)] = 1,
            [(MediaCleaner.Core.MediaItemKind.Movie, MediaCleaner.Core.CleanupRuleTriggerKind.NotPlayed)] = 2,
            [(MediaCleaner.Core.MediaItemKind.Episode, MediaCleaner.Core.CleanupRuleTriggerKind.Played)] = 3,
            [(MediaCleaner.Core.MediaItemKind.Episode, MediaCleaner.Core.CleanupRuleTriggerKind.NotPlayed)] = 4,
            [(MediaCleaner.Core.MediaItemKind.Video, MediaCleaner.Core.CleanupRuleTriggerKind.Played)] = 5,
            [(MediaCleaner.Core.MediaItemKind.Video, MediaCleaner.Core.CleanupRuleTriggerKind.NotPlayed)] = 6,
            [(MediaCleaner.Core.MediaItemKind.Audio, MediaCleaner.Core.CleanupRuleTriggerKind.Played)] = 7,
            [(MediaCleaner.Core.MediaItemKind.Audio, MediaCleaner.Core.CleanupRuleTriggerKind.NotPlayed)] = 8,
            [(MediaCleaner.Core.MediaItemKind.AudioBook, MediaCleaner.Core.CleanupRuleTriggerKind.Played)] = 9,
            [(MediaCleaner.Core.MediaItemKind.AudioBook, MediaCleaner.Core.CleanupRuleTriggerKind.NotPlayed)] = 10,
        };

        policy.Rules.Should().HaveCount(expected.Count);
        foreach (var rule in policy.Rules)
        {
            rule.Filters.MediaKinds.Should().ContainSingle();
            expected[(rule.Filters.MediaKinds.Single(), rule.Trigger.Kind)].Should().Be(rule.Trigger.Days);
            rule.Trigger.CountAsNotPlayedAfter.Should().Be(45);
        }

        policy.Rules.Where(x => x.Filters.MediaKinds.Single() == MediaCleaner.Core.MediaItemKind.Episode)
            .Should().OnlyContain(x => x.Filters.DeleteEpisodes == MediaCleaner.Core.SeriesDeleteKind.SeriesEnded
                && x.Filters.KeepSeriesKind == MediaCleaner.Core.SeriesKeepKind.Last);
    }

    [Theory]
    [InlineData(ConfigFavoriteKeepKind.DontKeep, MediaCleaner.Core.RuleFavoriteFilterKind.Ignore)]
    [InlineData(ConfigFavoriteKeepKind.AnyUser, MediaCleaner.Core.RuleFavoriteFilterKind.NotFavoriteByAnyUser)]
    [InlineData(ConfigFavoriteKeepKind.AllUsers, MediaCleaner.Core.RuleFavoriteFilterKind.NotFavoriteByAllUsers)]
    public void ToCleanupPolicy_MigratesEveryFavoriteMode(
        ConfigFavoriteKeepKind legacy,
        MediaCleaner.Core.RuleFavoriteFilterKind expected)
    {
        var config = new PluginConfiguration
        {
            ConfigVersion = 1,
            KeepMoviesFor = 1,
            KeepFavoriteMovies = legacy,
        };

        config.ToCleanupPolicy().Rules.Should().ContainSingle()
            .Which.Filters.FavoriteFilter.Should().Be(expected);
    }

    [Fact]
    public void ToCleanupPolicy_PreservesEmptyInclusionTagAsActiveRestriction()
    {
        var config = new PluginConfiguration
        {
            ConfigVersion = 1,
            KeepMoviesFor = 1,
            EnableTagExclusion = true,
            TagFilterMode = ConfigTagMode.Inclusion,
            InclusionTag = " ",
        };

        var rule = config.ToCleanupPolicy().Rules.Should().ContainSingle().Subject;

        rule.Filters.EnableTagFilter.Should().BeTrue();
        rule.Filters.TagFilterMode.Should().Be(MediaCleaner.Core.TagMode.Inclusion);
        rule.Filters.Tags.Should().BeEmpty();
    }

    [Fact]
    public void ToCleanupPolicy_MapsCurrentRuleConfiguration()
    {
        var config = new PluginConfiguration
        {
            ConfigVersion = 2,
            Rules =
            [
                new CleanupRuleConfiguration
                {
                    Id = "custom",
                    Name = "custom rule",
                    Enabled = false,
                    Trigger = new CleanupRuleTriggerConfiguration
                    {
                        Kind = ConfigRuleTriggerKind.AddedAge,
                        Days = 5,
                    },
                    Filters = new CleanupRuleFiltersConfiguration
                    {
                        MediaKinds = [ConfigRuleMediaKind.AudioBook],
                        FavoriteFilter = ConfigRuleFavoriteFilterKind.FavoriteByAllUsers,
                        DeleteEpisodes = ConfigSeriesDeleteKind.Episode,
                    },
                    Actions = new CleanupRuleActionsConfiguration
                    {
                        Kind = ConfigRuleActionKind.Protect,
                    },
                },
            ],
        };

        var policy = config.ToCleanupPolicy();

        var rule = policy.Rules.Should().ContainSingle().Subject;
        rule.Id.Should().Be("custom");
        rule.Name.Should().Be("custom rule");
        rule.Enabled.Should().BeFalse();
        rule.Trigger.Kind.Should().Be(MediaCleaner.Core.CleanupRuleTriggerKind.AddedAge);
        rule.Trigger.Days.Should().Be(5);
        rule.Filters.MediaKinds.Should().Equal(MediaCleaner.Core.MediaItemKind.AudioBook);
        rule.Filters.FavoriteFilter.Should().Be(MediaCleaner.Core.RuleFavoriteFilterKind.FavoriteByAllUsers);
        rule.Actions.Kind.Should().Be(MediaCleaner.Core.CleanupRuleActionKind.Protect);
    }
}
