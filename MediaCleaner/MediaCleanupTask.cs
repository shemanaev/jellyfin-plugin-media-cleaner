using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using MediaCleaner.Configuration;
using MediaCleaner.Filtering;
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
    private readonly StructuredConfig _config = StructuredConfig.CreateFromConfiguration(Plugin.Instance!.Configuration);
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
        _logger.LogDebug("UsersPlayedMode: {Mode}", _config.UsersIgnore.Mode);
        _logger.LogDebug("UsersIgnorePlayed: {Users}",
            userManager.Users
                .Where(x => _config.UsersIgnore.Users.Contains(x.Id.ToString("N")))
                .Select(x => $"{x.Username}={x.Id}")
        );
        _logger.LogDebug("UsersFavoritedMode: {Mode}", _config.UsersFavorites.Mode);
        _logger.LogDebug("UsersIgnoreFavorited: {Users}",
            userManager.Users
                .Where(x => _config.UsersFavorites.Users.Contains(x.Id.ToString("N")))
                .Select(x => $"{x.Username}={x.Id}")
        );

        var users = userManager.Users
            .Where(x => FilterUsersList(_config.UsersIgnore.Users, _config.UsersIgnore.Mode, x))
            .ToList();
        var usersWithFavorites = userManager.Users
            .Where(x => FilterUsersList(_config.UsersFavorites.Users, _config.UsersFavorites.Mode, x))
            .ToList();

        if (users.Count == 0)
        {
            _logger.LogInformation("Zero users. Skipping...");
            progress.Report(100);
            return;
        }

        DateTime? startDate = null;
        if (_config.Misc.CountAsNotPlayedAfter >= 0)
        {
            startDate = DateTime.Now.AddDays(-_config.Misc.CountAsNotPlayedAfter);
        }

        var expired = new List<ExpiredItem>();
        var leavingSoon = new List<ExpiredItem>();

        var itemsAdapter = new ItemsAdapter(loggerFactory.CreateLogger<ItemsAdapter>(), libraryManager, userDataManager, _config.Misc.AllowDeleteIfPlayedBeforeAdded);

        var progressCurrent = 0;
        var progressStep = 75 / _config.MediaNodes.Count;

        foreach (var mediaConfig in _config.MediaNodes)
        {
            _logger.LogTrace("Processing media config: {Name} -> {MediaConfig}", mediaConfig.ItemKind, mediaConfig);

            if (mediaConfig.KeepDays >= 0)
            {
                var collector = JunkCollectorFactory.CreateInstance(loggerFactory, itemsAdapter, mediaConfig.ItemKind, mediaConfig.Reason);
                var isNotPlayed = mediaConfig.Reason == ExpiredReason.NotPlayed;
                var items = collector.Execute(users, startDate, cancellationToken);
                var filters = GetFilters(loggerFactory, users, usersWithFavorites, mediaConfig, isNotPlayed, _config.Tags);
                var expiredItems = _filterService.Execute(items, filters, cancellationToken);

                if (_config.LeavingSoon.Days >= 0)
                {
                    _logger.LogInformation("Leaving soon started for {MediaConfig}", mediaConfig.ItemKind);
                    var leavingSoonFilters = GetFilters(loggerFactory, users, usersWithFavorites, GetLeavingSoonConfig(mediaConfig), isNotPlayed, _config.Tags);
                    var leavingSoonItems = _filterService.Execute(items, leavingSoonFilters, cancellationToken);
                    leavingSoon.AddRange(leavingSoonItems);
                    _logger.LogInformation("Leaving soon finished for {MediaConfig}", mediaConfig.ItemKind);
                }

                expired.AddRange(expiredItems);
            }

            progressCurrent += progressStep;
            progress.Report(progressCurrent);
        }

        var expiredIds = expired.Select(x => x.Item.Id).ToHashSet();
        leavingSoon.RemoveAll(item => expiredIds.Contains(item.Item.Id));
        _leavingSoonCollectionService.AddItemRange(leavingSoon.Select(x => x.Item.Id));
        if (!IsDryRun) await _leavingSoonCollectionService.Finish();
        progress.Report(85);

        var deletionItems = expired.OrderBy(x => x.Reason)
            .ThenBy(x => x.Reason == ExpiredReason.Played ? x.Data.First().LastPlayedDate : x.Item.DateCreated)
            .ToList();

        foreach (var item in deletionItems)
        {
            LogDeletion(item);

            if (IsDryRun) continue;

            await _notificationService.CreateNotification(item);

            if (_config.Misc.MarkAsUnplayed)
            {
                foreach (var data in item.Data)
                {
                    item.Item.MarkUnplayed(data.User);
                }
            }

            DeleteItem(item);
        }

        progress.Report(100);
    }

    private ConfigMediaNode GetLeavingSoonConfig(ConfigMediaNode config) =>
        config with { KeepDays = config.KeepDays - _config.LeavingSoon.Days };

    private static bool FilterUsersList(List<string> users, UsersListMode mode, User x) =>
        users.Contains(x.Id.ToString("N")) switch
        {
            true when mode == UsersListMode.Ignore => false,
            true when mode == UsersListMode.Acknowledge => true,
            false when mode == UsersListMode.Ignore => true,
            false when mode == UsersListMode.Acknowledge => false,
            _ => throw new NotImplementedException(),
        };

    private List<IExpiredItemFilter> GetFilters(ILoggerFactory loggerFactory, List<User> users, List<User> usersWithFavorites, ConfigMediaNode config, bool isNotPlayed, ConfigTagsNode configTags)
    {
        var filters = new List<IExpiredItemFilter>();

        if (isNotPlayed)
        {
            filters.Add(new ExpiredNotPlayedFilter(loggerFactory.CreateLogger<ExpiredNotPlayedFilter>(), config.KeepDays));
        }
        else
        {
            filters.Add(new ExpiredFilter(loggerFactory.CreateLogger<ExpiredFilter>(), config.KeepDays, users.Count, config.UserMode));
        }

        filters.Add(new FavoritesFilter(loggerFactory.CreateLogger<FavoritesFilter>(), config.FavoriteMode, usersWithFavorites, userDataManager));

        filters.Add(new LocationsFilter(loggerFactory.CreateLogger<LocationsFilter>(),
            _config.Locations.Mode,
            _config.Locations.Locations,
            fileSystem));

        if (config.SpecificOptions is not null)
        {
            // TODO: more generic approach for different types
            var specificOptions = (config.SpecificOptions as ConfigSeriesSpecificNode)!;
            filters.AddRange(new SeriesFilter(loggerFactory.CreateLogger<SeriesFilter>(), specificOptions.GroupMode, specificOptions.KeepMode));
        }

        if (configTags.Enabled)
        {
            string tagName = configTags.Mode == TagMode.Exclusion
                ? configTags.ExcludeTag
                : configTags.IncludeTag;
            _logger.LogDebug("Adding tag filter with mode: {TagMode}, tag: {TagName}", configTags.Mode, tagName);
            filters.Add(new TagFilter(loggerFactory.CreateLogger<TagFilter>(), tagName, configTags.Mode));
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

    private void DeleteItem(ExpiredItem item)
    {
        var opts = new DeleteOptions { DeleteFileLocation = true };

        try
        {
            switch (item.Item)
            {
                case Season season:
                    foreach (var eps in season.GetEpisodes())
                    {
                        libraryManager.DeleteItem(eps, opts, true);
                    }

                    libraryManager.DeleteItem(season, opts, true);
                    if (!(season.Series?.GetEpisodes().Any() ?? false) && !HasExtraFiles(season.Series))
                    {
                        libraryManager.DeleteItem(season.Series!, opts, true);
                    }

                    break;

                case Episode episode:
                    libraryManager.DeleteItem(episode, opts, true);
                    if (!episode.Season?.GetEpisodes().Any() ?? false)
                    {
                        libraryManager.DeleteItem(episode.Season!, opts, true);
                    }

                    if (!(episode.Series?.GetEpisodes().Any() ?? false) && !HasExtraFiles(episode.Series))
                    {
                        libraryManager.DeleteItem(episode.Series!, opts, true);
                    }

                    break;

                default:
                    libraryManager.DeleteItem(item.Item, opts, true);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting item: {name}", item.FullName);
        }
    }

    /// <summary>
    /// Check if item has extra files and shouldn't be deleted.
    /// </summary>
    private static bool HasExtraFiles(BaseItem? item)
    {
        if (item == null) return false;
        if (!Directory.Exists(item.Path)) return false;

        // https://github.com/jmbannon/ytdl-sub
        var hasYtdlSubMeta = Directory.EnumerateFiles(item.Path, ".ytdl-sub-*-download-archive.json").Any();

        return hasYtdlSubMeta;
    }
}
