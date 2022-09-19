using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Tasks;
using MediaCleaner.JunkCollectors;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.IO;
using MediaCleaner.Filtering;

namespace MediaCleaner
{
    public class MediaCleanupTask : IScheduledTask
    {
        private readonly ILogger _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserDataManager _userDataManager;
        private readonly IActivityManager _activityManager;
        private readonly ILocalizationManager _localization;
        private readonly IFileSystem _fileSystem;

        private readonly ItemsAdapter _itemsAdapter;

        private readonly IJunkCollector _moviesCollector;
        private readonly IJunkCollector _seriesCollector;
        private readonly IJunkCollector _videosCollector;

        public bool IsDryRun { get; set; } = false;

        public string Name => "Played media cleanup";

        public string Description => "Delete played media files after specified amount of time";

        public string Key => "MediaCleanup";

        public string Category => _localization.GetLocalizedString("TasksMaintenanceCategory");

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerInterval,
                    IntervalTicks = TimeSpan.FromDays(1).Ticks
                }
            };

        public MediaCleanupTask(
            IUserManager userManager,
            ILoggerFactory loggerFactory,
            ILibraryManager libraryManager,
            IUserDataManager userDataManager,
            IActivityManager activityManager,
            ILocalizationManager localization,
            IFileSystem fileSystem)
        {
            _logger = loggerFactory.CreateLogger<MediaCleanupTask>();
            _loggerFactory = loggerFactory;
            _userManager = userManager;
            _libraryManager = libraryManager;
            _activityManager = activityManager;
            _userDataManager = userDataManager;
            _localization = localization;
            _fileSystem = fileSystem;

            _itemsAdapter = new ItemsAdapter(loggerFactory.CreateLogger<ItemsAdapter>(), libraryManager, userDataManager);

            _moviesCollector = new MoviesJunkCollector(loggerFactory.CreateLogger<MoviesJunkCollector>(), _itemsAdapter);
            _seriesCollector = new SeriesJunkCollector(loggerFactory.CreateLogger<SeriesJunkCollector>(), _itemsAdapter);
            _videosCollector = new VideosJunkCollector(loggerFactory.CreateLogger<VideosJunkCollector>(), _itemsAdapter);
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var users = _userManager.Users
                .Where(x => !Plugin.Instance.Configuration.UsersIgnorePlayed.Contains(x.Id.ToString("N")))
                .ToList();
            var usersWithFavorites = _userManager.Users
                .Where(x => !Plugin.Instance.Configuration.UsersIgnoreFavorited.Contains(x.Id.ToString("N")))
                .ToList();

            if (users.Count == 0)
            {
                _logger.LogInformation("Zero users. Skipping...");
                progress.Report(100);
                return;
            }

            var expired = new List<ExpiredItem>();

            if (Plugin.Instance.Configuration.KeepMoviesFor >= 0)
            {
                var filters = new List<IExpiredItemFilter>
                {
                    new ExpiredFilter(Plugin.Instance.Configuration.KeepMoviesFor),
                    new FavoritesFilter(Plugin.Instance.Configuration.KeepFavoriteMovies, usersWithFavorites, _userDataManager),
                    new LocationsFilter(Plugin.Instance.Configuration.LocationsMode, Plugin.Instance.Configuration.LocationsExcluded, _fileSystem)
                };
                var expiredMovies = _moviesCollector.Execute(users, filters, cancellationToken);
                expired.AddRange(expiredMovies);
            }
            progress.Report(25);

            if (Plugin.Instance.Configuration.KeepEpisodesFor >= 0)
            {
                var filters = new List<IExpiredItemFilter>
                {
                    new ExpiredFilter(Plugin.Instance.Configuration.KeepEpisodesFor),
                    new FavoritesFilter(Plugin.Instance.Configuration.KeepFavoriteEpisodes, usersWithFavorites, _userDataManager),
                    new LocationsFilter(Plugin.Instance.Configuration.LocationsMode, Plugin.Instance.Configuration.LocationsExcluded, _fileSystem),
                    new SeriesFilter(_loggerFactory.CreateLogger<SeriesFilter>(), Plugin.Instance.Configuration.DeleteEpisodes)
                };
                var expiredSeries = _seriesCollector.Execute(users, filters, cancellationToken);
                expired.AddRange(expiredSeries);
            }
            progress.Report(50);

            if (Plugin.Instance.Configuration.KeepVideosFor >= 0)
            {
                var filters = new List<IExpiredItemFilter>
                {
                    new ExpiredFilter(Plugin.Instance.Configuration.KeepVideosFor),
                    new FavoritesFilter(Plugin.Instance.Configuration.KeepFavoriteVideos, usersWithFavorites, _userDataManager),
                    new LocationsFilter(Plugin.Instance.Configuration.LocationsMode, Plugin.Instance.Configuration.LocationsExcluded, _fileSystem)
                };
                var expiredVideos = _videosCollector.Execute(users, filters, cancellationToken);
                expired.AddRange(expiredVideos);
            }
            progress.Report(75);

            expired = expired.GroupBy(x => x.Item.Id)
                .Select(x => x.OrderByDescending(m => m.LastPlayedDate).First())
                .OrderBy(x => x.LastPlayedDate)
                .ToList();

            foreach (var item in expired)
            {
                _logger.LogInformation("({Type}) '{Name}' will be deleted because expired for {Username} ({LastPlayedDate})",
                    item.Item.GetType().Name, item.Item.Name, item.User.Username, item.LastPlayedDate);

                if (!IsDryRun)
                {
                    await CreateNotification(item);
                    DeleteItem(item.Item);
                }
            }
            progress.Report(100);
        }

        private void DeleteItem(BaseItem item)
        {
            var opts = new DeleteOptions { DeleteFileLocation = true };
            switch (item)
            {
                case Movie movie:
                    _libraryManager.DeleteItem(movie, opts, true);
                    break;

                case Series series:
                    _libraryManager.DeleteItem(series, opts, true);
                    break;

                case Season season:
                    foreach (var eps in season.GetEpisodes())
                    {
                        _libraryManager.DeleteItem(eps, opts, true);
                    }
                    _libraryManager.DeleteItem(season, opts, true);
                    if (!season.Series.GetEpisodes().Any())
                    {
                        _libraryManager.DeleteItem(season.Series, opts, true);
                    }
                    break;

                case Episode episode:
                    _libraryManager.DeleteItem(episode, opts, true);
                    if (!episode.Season.GetEpisodes().Any())
                    {
                        _libraryManager.DeleteItem(episode.Season, opts, true);
                    }
                    if (!episode.Series.GetEpisodes().Any())
                    {
                        _libraryManager.DeleteItem(episode.Series, opts, true);
                    }
                    break;

                case Video video:
                    _libraryManager.DeleteItem(video, opts, true);
                    break;

                default:
                    _libraryManager.DeleteItem(item, opts, true);
                    break;
            }
        }

        private async Task CreateNotification(ExpiredItem item)
        {
            string title;
            string shortOverview;
            string overview;

            switch (item.Item)
            {
                case Movie movie:
                    title = $"\"{movie.Name}\" was deleted";
                    shortOverview = $"Last played by {item.User.Username} at {item.LastPlayedDate}";
                    overview = $"{movie.Path}";
                    break;

                case Series series:
                    title = $"\"{series.Name}\" was deleted";
                    shortOverview = $"Last played by {item.User.Username} at {item.LastPlayedDate}";
                    overview = $"{series.Path}";
                    break;

                case Season season:
                    title = $"\"{season.SeriesName}\" S{season.IndexNumber:D2} was deleted";
                    shortOverview = $"Last played by {item.User.Username} at {item.LastPlayedDate}";
                    overview = $"{season.Path ?? season.SeriesPath}";
                    break;

                case Episode episode:
                    title = $"\"{episode.SeriesName}\" S{episode.ParentIndexNumber:D2}E{episode.IndexNumber:D2} was deleted";
                    shortOverview = $"Last played by {item.User.Username} at {item.LastPlayedDate}";
                    overview = $"{episode.Path}";
                    break;

                case Video video:
                    title = $"\"{video.Name}\" was deleted";
                    shortOverview = $"Last played by {item.User.Username} at {item.LastPlayedDate}";
                    overview = $"{video.Path}";
                    break;

                default:
                    title = $"\"{item.Item.Name}\" was deleted";
                    shortOverview = $"Last played by {item.User.Username} at {item.LastPlayedDate}";
                    overview = $"{item.Item.Path}";
                    break;
            };

            await _activityManager.CreateAsync(new ActivityLog(title, "MediaCleaner", Guid.Empty)
            {
                ShortOverview = shortOverview,
                Overview = overview,
            });
        }
    }
}
