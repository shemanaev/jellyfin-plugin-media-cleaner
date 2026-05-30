using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using MediaCleaner.Compatibility;
using MediaCleaner.Configuration;
using MediaCleaner.Filtering;
using MediaCleaner.Integrations;
using MediaCleaner.JunkCollectors;
using MediaCleaner.Models;
using Microsoft.Extensions.Logging;

namespace MediaCleaner;

public class MediaCleanupTask(
    IUserManager userManager,
    ILoggerFactory loggerFactory,
    ILibraryManager libraryManager,
    IUserDataManager userDataManager,
    IActivityManager activityManager,
    ILocalizationManager localization,
    IFileSystem fileSystem,
    ICollectionManager collectionManager)
    : IScheduledTask
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<MediaCleanupTask>();
    private readonly NotificationService _notificationService = new(activityManager);
    private readonly LeavingSoonCollectionService _leavingSoonCollectionService = new(loggerFactory.CreateLogger<LeavingSoonCollectionService>(), libraryManager, collectionManager);
    private readonly FilterService _filterService = new(loggerFactory.CreateLogger<FilterService>());

    public bool IsDryRun { get; init; }

    public string Name => "Played media cleanup";

    public string Description => "Delete played media files after specified amount of time";

    public string Key => "MediaCleanup";

    public string Category => localization.GetLocalizedString("TasksMaintenanceCategory");

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new() { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromDays(1).Ticks }
    ];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = StructuredConfig.CreateFromConfiguration(Plugin.Instance!.Configuration);
        var deletionService = ArrDeletionService.Create(loggerFactory, config.Arr);
        var allUsers = UserManagerCompatibility.GetUsers(userManager);

        _logger.LogInformation(
            "Media cleanup task started. Dry run: {IsDryRun}. LeavingSoonDays: {LeavingSoonDays}",
            IsDryRun,
            config.LeavingSoon.Days);

        if (config.LeavingSoon.Days < 0)
        {
            _logger.LogInformation("Leaving Soon collection update disabled.");
        }

        _logger.LogDebug("UsersPlayedMode: {Mode}", config.UsersIgnore.Mode);
        _logger.LogDebug("UsersIgnorePlayed: {Users}",
            allUsers
                .Where(x => config.UsersIgnore.Users.Contains(x.Id.ToString("N")))
                .Select(x => $"{x.Username}={x.Id}")
        );
        _logger.LogDebug("UsersFavoritedMode: {Mode}", config.UsersFavorites.Mode);
        _logger.LogDebug("UsersIgnoreFavorited: {Users}",
            allUsers
                .Where(x => config.UsersFavorites.Users.Contains(x.Id.ToString("N")))
                .Select(x => $"{x.Username}={x.Id}")
        );

        var users = allUsers
            .Where(x => FilterUsersList(config.UsersIgnore.Users, config.UsersIgnore.Mode, x))
            .ToList();
        var usersWithFavorites = allUsers
            .Where(x => FilterUsersList(config.UsersFavorites.Users, config.UsersFavorites.Mode, x))
            .ToList();

        if (users.Count == 0)
        {
            _logger.LogInformation("Zero users. Skipping...");
            progress.Report(100);
            return;
        }

        DateTime? startDate = null;
        if (config.Misc.CountAsNotPlayedAfter >= 0)
        {
            startDate = DateTime.Now.AddDays(-config.Misc.CountAsNotPlayedAfter);
        }

        var expired = new List<ExpiredItem>();
        var leavingSoon = new List<ExpiredItem>();

        var itemsAdapter = new ItemsAdapter(loggerFactory.CreateLogger<ItemsAdapter>(), libraryManager, userDataManager, config.Misc.AllowDeleteIfPlayedBeforeAdded);

        var progressCurrent = 0;
        var progressStep = 75 / config.MediaNodes.Count;

        foreach (var mediaConfig in config.MediaNodes)
        {
            _logger.LogTrace("Processing media config: {Name} -> {MediaConfig}", mediaConfig.ItemKind, mediaConfig);

            if (mediaConfig.KeepDays >= 0)
            {
                var collector = JunkCollectorFactory.CreateInstance(loggerFactory, itemsAdapter, mediaConfig.ItemKind, mediaConfig.Reason);
                var isNotPlayed = mediaConfig.Reason == ExpiredReason.NotPlayed;
                var items = collector.Execute(users, startDate, cancellationToken);
                var filters = GetFilters(loggerFactory, users, usersWithFavorites, mediaConfig, isNotPlayed, config);
                var expiredItems = _filterService.Execute(items, filters, cancellationToken);

                if (config.LeavingSoon.Days >= 0)
                {
                    var leavingSoonConfig = GetLeavingSoonConfig(mediaConfig, config);
                    _logger.LogInformation(
                        "Leaving Soon candidate selection started for {MediaConfig}. KeepDays: {KeepDays}, LeavingSoonKeepDays: {LeavingSoonKeepDays}",
                        mediaConfig.ItemKind,
                        mediaConfig.KeepDays,
                        leavingSoonConfig.KeepDays);
                    var leavingSoonFilters = GetFilters(loggerFactory, users, usersWithFavorites, leavingSoonConfig, isNotPlayed, config);
                    var leavingSoonItems = _filterService.Execute(items, leavingSoonFilters, cancellationToken);
                    leavingSoon.AddRange(leavingSoonItems);
                    _logger.LogInformation(
                        "Leaving Soon candidate selection finished for {MediaConfig}: {Count} candidate(s) before deletion exclusion.",
                        mediaConfig.ItemKind,
                        leavingSoonItems.Count);
                }

                expired.AddRange(expiredItems);
            }

            progressCurrent += progressStep;
            progress.Report(progressCurrent);
        }

        var leavingSoonCandidatesBeforeExpiredExclusion = leavingSoon.Count;
        var expiredIds = expired.Select(x => x.Item.Id).ToHashSet();
        leavingSoon.RemoveAll(item => expiredIds.Contains(item.Item.Id));
        var leavingSoonExcludedAsExpired = leavingSoonCandidatesBeforeExpiredExclusion - leavingSoon.Count;
        _logger.LogInformation(
            "Leaving Soon candidates: {CandidateCount} candidate(s), {ExcludedCount} excluded because already due for deletion.",
            leavingSoon.Count,
            leavingSoonExcludedAsExpired);
        _leavingSoonCollectionService.AddItemRange(leavingSoon.Select(x => x.Item.Id));
        if (IsDryRun)
        {
            _logger.LogInformation("Dry run: Leaving Soon collection update skipped.");
        }
        else
        {
            await _leavingSoonCollectionService.Finish();
        }

        progress.Report(85);

        var deletionItems = expired.OrderBy(x => x.Reason)
            .ThenBy(x => x.Reason == ExpiredReason.Played ? x.Data.First().LastPlayedDate : x.Item.DateCreated)
            .ToList();

        foreach (var item in deletionItems)
        {
            LogDeletion(item);

            var deletionResult = await deletionService.DeleteAsync(item, IsDryRun, cancellationToken);
            LogDeletionResult(item, deletionResult);

            if (IsDryRun || deletionResult.Status != ArrDeletionStatus.Deleted)
            {
                continue;
            }

            var itemData = item.Data;
            if (config.Misc.MarkAsUnplayed && itemData is not null)
            {
                foreach (var data in itemData)
                {
                    item.Item.MarkUnplayed(data.User);
                }
            }

            await _notificationService.CreateNotification(item);
        }

        progress.Report(100);
    }

    private static ConfigMediaNode GetLeavingSoonConfig(ConfigMediaNode mediaConfig, StructuredConfig config) =>
        mediaConfig with { KeepDays = mediaConfig.KeepDays - config.LeavingSoon.Days };

    private static bool FilterUsersList(List<string> users, UsersListMode mode, User x) =>
        users.Contains(x.Id.ToString("N")) switch
        {
            true when mode == UsersListMode.Ignore => false,
            true when mode == UsersListMode.Acknowledge => true,
            false when mode == UsersListMode.Ignore => true,
            false when mode == UsersListMode.Acknowledge => false,
            _ => throw new NotImplementedException(),
        };

    private List<IExpiredItemFilter> GetFilters(ILoggerFactory loggerFactory, List<User> users, List<User> usersWithFavorites, ConfigMediaNode mediaConfig, bool isNotPlayed, StructuredConfig config)
    {
        var filters = new List<IExpiredItemFilter>();

        if (isNotPlayed)
        {
            filters.Add(new ExpiredNotPlayedFilter(loggerFactory.CreateLogger<ExpiredNotPlayedFilter>(), mediaConfig.KeepDays));
        }
        else
        {
            filters.Add(new ExpiredFilter(loggerFactory.CreateLogger<ExpiredFilter>(), mediaConfig.KeepDays, users.Count, mediaConfig.UserMode));
        }

        filters.Add(new FavoritesFilter(loggerFactory.CreateLogger<FavoritesFilter>(), mediaConfig.FavoriteMode, usersWithFavorites, userDataManager));

        filters.Add(new LocationsFilter(loggerFactory.CreateLogger<LocationsFilter>(),
            config.Locations.Mode,
            config.Locations.Locations,
            fileSystem));

        if (mediaConfig.SpecificOptions is not null)
        {
            // TODO: more generic approach for different types
            var specificOptions = (mediaConfig.SpecificOptions as ConfigSeriesSpecificNode)!;
            filters.AddRange(new SeriesFilter(loggerFactory.CreateLogger<SeriesFilter>(), specificOptions.GroupMode, specificOptions.KeepMode));
        }

        if (config.Tags.Enabled)
        {
            string tagName = config.Tags.Mode == TagMode.Exclusion
                ? config.Tags.ExcludeTag
                : config.Tags.IncludeTag;
            _logger.LogDebug("Adding tag filter with mode: {TagMode}, tag: {TagName}", config.Tags.Mode, tagName);
            filters.Add(new TagFilter(loggerFactory.CreateLogger<TagFilter>(), tagName, config.Tags.Mode));
        }

        return filters;
    }

    private void LogDeletion(ExpiredItem item)
    {
        switch (item.Reason)
        {
            case ExpiredReason.Played:
                var expiredForUsers = string.Join(", ", item.Data.Select(x => $"{x.User.Username} ({x.LastPlayedDate.ToLocalTime()})"));
                _logger.LogInformation("({Type}) \"{Name}\" will be deleted because expired for: {Users}",
                    item.Item.GetType().Name, item.FullName, expiredForUsers);
                break;

            case ExpiredReason.NotPlayed:
                _logger.LogInformation("({Type}) \"{Name}\" will be deleted because no one played it since {DateCreated}",
                    item.Item.GetType().Name, item.FullName, item.Item.DateCreated.ToLocalTime());
                break;
        }
    }

    private void LogDeletionResult(ExpiredItem item, ArrDeletionResult result)
    {
        switch (result.Status)
        {
            case ArrDeletionStatus.Planned:
                _logger.LogInformation("{Name}: {Message}", item.FullName, result.Message);
                break;
            case ArrDeletionStatus.Deleted:
                _logger.LogInformation("{Name}: {Message}", item.FullName, result.Message);
                break;
            case ArrDeletionStatus.Skipped:
                _logger.LogWarning("{Name}: {Message}", item.FullName, result.Message);
                break;
            case ArrDeletionStatus.Failed:
                _logger.LogError("{Name}: {Message}", item.FullName, result.Message);
                break;
        }
    }
}
