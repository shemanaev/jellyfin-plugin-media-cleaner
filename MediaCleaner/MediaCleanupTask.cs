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
using MediaCleaner.Configuration;

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

        private static PluginConfiguration Configuration =>
            Plugin.Instance!.Configuration;

        public bool IsDryRun { get; init; }

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
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            _logger.LogDebug("UsersIgnorePlayed: {Users}",
                 _userManager.Users
                    .Where(x => Configuration.UsersIgnorePlayed.Contains(x.Id.ToString("N")))
                    .Select(x => $"{x.Username}={x.Id}")
            );
            _logger.LogDebug("UsersIgnoreFavorited: {Users}",
                 _userManager.Users
                    .Where(x => Configuration.UsersIgnoreFavorited.Contains(x.Id.ToString("N")))
                    .Select(x => $"{x.Username}={x.Id}")
            );

            var users = _userManager.Users
                .Where(x => !Configuration.UsersIgnorePlayed.Contains(x.Id.ToString("N")))
                .ToList();
            var usersWithFavorites = _userManager.Users
                .Where(x => !Configuration.UsersIgnoreFavorited.Contains(x.Id.ToString("N")))
                .ToList();

            if (users.Count == 0)
            {
                _logger.LogInformation("Zero users. Skipping...");
                progress.Report(100);
                return;
            }

            var expired = new List<ExpiredItem>();

            var itemsAdapter = new ItemsAdapter(_loggerFactory.CreateLogger<ItemsAdapter>(), _libraryManager, _userDataManager);

            if (Configuration.KeepMoviesFor >= 0)
            {
                var expiredMovies = CollectMovies(users, usersWithFavorites, itemsAdapter, cancellationToken);
                expired.AddRange(expiredMovies);
            }
            progress.Report(25);

            if (Configuration.KeepEpisodesFor >= 0)
            {
                var expiredSeries = CollectSeries(users, usersWithFavorites, itemsAdapter, cancellationToken);
                expired.AddRange(expiredSeries);
            }
            progress.Report(50);

            if (Configuration.KeepVideosFor >= 0)
            {
                var expiredVideos = CollectVideos(users, usersWithFavorites, itemsAdapter, cancellationToken);
                expired.AddRange(expiredVideos);
            }
            progress.Report(75);

            expired = expired.GroupBy(x => x.Item.Id)
                .Select(x => x.OrderByDescending(m => m.LastPlayedDate).First())
                .OrderBy(x => x.LastPlayedDate)
                .ToList();

            foreach (var item in expired)
            {
                _logger.LogInformation("({Type}) \"{Name}\" will be deleted because expired for \"{Username}\" ({LastPlayedDate})",
                    item.Item.GetType().Name, item.Item.Name, item.User.Username, item.LastPlayedDate);

                if (IsDryRun) continue;

                await CreateNotification(item);

                if (Configuration.MarkAsUnplayed)
                {
                    item.Item.MarkUnplayed(item.User);
                }

                DeleteItem(item.Item);
            }
            progress.Report(100);
        }

        private List<ExpiredItem> CollectMovies(List<User> users, List<User> usersWithFavorites, ItemsAdapter itemsAdapter, CancellationToken cancellationToken)
        {
            var filters = new List<IExpiredItemFilter>
                {
                    new ExpiredFilter(Configuration.KeepMoviesFor),
                    new FavoritesFilter(_loggerFactory.CreateLogger<FavoritesFilter>(),
                        Configuration.KeepFavoriteMovies,
                        usersWithFavorites,
                        _userDataManager),
                    new LocationsFilter(_loggerFactory.CreateLogger<LocationsFilter>(),
                        Configuration.LocationsMode,
                        Configuration.LocationsExcluded,
                        _fileSystem)
                };
            var moviesCollector = new MoviesJunkCollector(_loggerFactory.CreateLogger<MoviesJunkCollector>(), itemsAdapter);
            var expiredMovies = moviesCollector.Execute(users, filters, cancellationToken);
            return expiredMovies;
        }

        private List<ExpiredItem> CollectSeries(List<User> users, List<User> usersWithFavorites, ItemsAdapter itemsAdapter, CancellationToken cancellationToken)
        {
            var filters = new List<IExpiredItemFilter>
                {
                    new ExpiredFilter(Configuration.KeepEpisodesFor),
                    new FavoritesFilter(_loggerFactory.CreateLogger<FavoritesFilter>(),
                        Configuration.KeepFavoriteEpisodes,
                        usersWithFavorites,
                        _userDataManager),
                    new LocationsFilter(_loggerFactory.CreateLogger<LocationsFilter>(),
                        Configuration.LocationsMode,
                        Configuration.LocationsExcluded,
                        _fileSystem),
                    new SeriesFilter(_loggerFactory.CreateLogger<SeriesFilter>(), Configuration.DeleteEpisodes)
                };
            var seriesCollector = new SeriesJunkCollector(_loggerFactory.CreateLogger<SeriesJunkCollector>(), itemsAdapter);
            var expiredSeries = seriesCollector.Execute(users, filters, cancellationToken);
            return expiredSeries;
        }

        private List<ExpiredItem> CollectVideos(List<User> users, List<User> usersWithFavorites, ItemsAdapter itemsAdapter, CancellationToken cancellationToken)
        {
            var filters = new List<IExpiredItemFilter>
                {
                    new ExpiredFilter(Configuration.KeepVideosFor),
                    new FavoritesFilter(_loggerFactory.CreateLogger<FavoritesFilter>(),
                        Configuration.KeepFavoriteVideos,
                        usersWithFavorites,
                        _userDataManager),
                    new LocationsFilter(_loggerFactory.CreateLogger<LocationsFilter>(),
                        Configuration.LocationsMode,
                        Configuration.LocationsExcluded,
                        _fileSystem)
                };
            var videosCollector = new VideosJunkCollector(_loggerFactory.CreateLogger<VideosJunkCollector>(), itemsAdapter);
            var expiredVideos = videosCollector.Execute(users, filters, cancellationToken);
            return expiredVideos;
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
                    if (!season.Series?.GetEpisodes().Any() ?? false)
                    {
                        _libraryManager.DeleteItem(season.Series, opts, true);
                    }
                    break;

                case Episode episode:
                    _libraryManager.DeleteItem(episode, opts, true);
                    if (!episode.Season?.GetEpisodes().Any() ?? false)
                    {
                        _libraryManager.DeleteItem(episode.Season, opts, true);
                    }
                    if (!episode.Series?.GetEpisodes().Any() ?? false)
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
