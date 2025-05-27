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
using System.IO;

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
            _logger.LogDebug("UsersPlayedMode: {Mode}", Configuration.UsersPlayedMode);
            _logger.LogDebug("UsersIgnorePlayed: {Users}",
                 _userManager.Users
                    .Where(x => Configuration.UsersIgnorePlayed.Contains(x.Id.ToString("N")))
                    .Select(x => $"{x.Username}={x.Id}")
            );
            _logger.LogDebug("UsersFavoritedMode: {Mode}", Configuration.UsersFavoritedMode);
            _logger.LogDebug("UsersIgnoreFavorited: {Users}",
                 _userManager.Users
                    .Where(x => Configuration.UsersIgnoreFavorited.Contains(x.Id.ToString("N")))
                    .Select(x => $"{x.Username}={x.Id}")
            );

            var users = _userManager.Users
                .Where(x => FilterUsersList(Configuration.UsersIgnorePlayed, Configuration.UsersPlayedMode, x))
                .ToList();
            var usersWithFavorites = _userManager.Users
                .Where(x => FilterUsersList(Configuration.UsersIgnoreFavorited, Configuration.UsersFavoritedMode, x))
                .ToList();

            if (users.Count == 0)
            {
                _logger.LogInformation("Zero users. Skipping...");
                progress.Report(100);
                return;
            }

            DateTime? startDate = null;
            if (Configuration.CountAsNotPlayedAfter >= 0)
            {
                startDate = DateTime.Now.AddDays(-Configuration.CountAsNotPlayedAfter);
            }

            var expired = new List<ExpiredItem>();
            var expiredNotPlayed = new List<ExpiredItem>();

            var itemsAdapter = new ItemsAdapter(_loggerFactory.CreateLogger<ItemsAdapter>(), _libraryManager, _userDataManager, Configuration);

            if (Configuration.KeepMoviesFor >= 0 || Configuration.KeepMoviesNotPlayedFor >= 0)
            {
                if (Configuration.KeepMoviesFor >= 0)
                {
                    var expiredMovies = CollectMovies(users, usersWithFavorites, itemsAdapter, startDate, cancellationToken);
                    expired.AddRange(expiredMovies);
                }
                if (Configuration.KeepMoviesNotPlayedFor >= 0)
                {
                    var expiredNotPlayedMovies = CollectNotPlayedMovies(users, usersWithFavorites, itemsAdapter, startDate, cancellationToken);
                    expiredNotPlayed.AddRange(expiredNotPlayedMovies);
                }
            }
            progress.Report(25);

            if (Configuration.KeepEpisodesFor >= 0 || Configuration.KeepEpisodesNotPlayedFor >= 0)
            {
                if (Configuration.KeepEpisodesFor >= 0)
                {
                    var expiredSeries = CollectSeries(users, usersWithFavorites, itemsAdapter, startDate, cancellationToken);
                    expired.AddRange(expiredSeries);
                }
                if (Configuration.KeepEpisodesNotPlayedFor >= 0)
                {
                    var expiredNotPlayedEpisodes = CollectNotPlayedSeries(users, usersWithFavorites, itemsAdapter, startDate, cancellationToken);
                    expiredNotPlayed.AddRange(expiredNotPlayedEpisodes);
                }
            }
            progress.Report(50);

            if (Configuration.KeepVideosFor >= 0 || Configuration.KeepVideosNotPlayedFor >= 0)
            {
                if (Configuration.KeepVideosFor >= 0)
                {
                    var expiredVideos = CollectVideos(users, usersWithFavorites, itemsAdapter, startDate, cancellationToken);
                    expired.AddRange(expiredVideos);
                }
                if (Configuration.KeepVideosNotPlayedFor >= 0)
                {
                    var expiredNotPlayedVideos = CollectNotPlayedVideos(users, usersWithFavorites, itemsAdapter, startDate, cancellationToken);
                    expiredNotPlayed.AddRange(expiredNotPlayedVideos);
                }
            }

            if (Configuration.KeepAudioFor >= 0 || Configuration.KeepAudioNotPlayedFor >= 0)
            {
                if (Configuration.KeepAudioFor >= 0)
                {
                    var expiredAudio = CollectAudio(users, usersWithFavorites, itemsAdapter, startDate, cancellationToken);
                    expired.AddRange(expiredAudio);
                }
                if (Configuration.KeepAudioNotPlayedFor >= 0)
                {
                    var expiredNotPlayedAudio = CollectNotPlayedAudio(users, usersWithFavorites, itemsAdapter, startDate, cancellationToken);
                    expiredNotPlayed.AddRange(expiredNotPlayedAudio);
                }
            }

            if (Configuration.KeepAudioBooksFor >= 0 || Configuration.KeepAudioBooksNotPlayedFor >= 0)
            {
                if (Configuration.KeepAudioBooksFor >= 0)
                {
                    var expiredAudioBooks = CollectAudioBook(users, usersWithFavorites, itemsAdapter, startDate, cancellationToken);
                    expired.AddRange(expiredAudioBooks);
                }
                if (Configuration.KeepAudioBooksNotPlayedFor >= 0)
                {
                    var expiredNotPlayedAudioBooks = CollectNotPlayedAudioBook(users, usersWithFavorites, itemsAdapter, startDate, cancellationToken);
                    expiredNotPlayed.AddRange(expiredNotPlayedAudioBooks);
                }
            }
            progress.Report(75);

            expired = expired.OrderBy(x => x.Data.First().LastPlayedDate).ToList();

            foreach (var item in expired)
            {
                var expiredForUsers = string.Join(", ", item.Data.Select(x => $"{x.User.Username} ({x.LastPlayedDate.ToLocalTime()})"));
                _logger.LogInformation("({Type}) \"{Name}\" will be deleted because expired for: {Users}",
                    item.Item.GetType().Name, item.FullName, expiredForUsers);

                if (IsDryRun) continue;

                await CreateNotification(item);

                if (Configuration.MarkAsUnplayed)
                {
                    foreach (var data in item.Data)
                    {
                        item.Item.MarkUnplayed(data.User);
                    }
                }

                try
                {
                    DeleteItem(item.Item);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error deleting item: {name}", item.FullName);
                }
            }

            expiredNotPlayed = expiredNotPlayed.OrderBy(x => x.Item.DateCreated).ToList();

            foreach (var item in expiredNotPlayed)
            {
                _logger.LogInformation("({Type}) \"{Name}\" will be deleted because no one played it since {DateCreated}",
                    item.Item.GetType().Name, item.FullName, item.Item.DateCreated.ToLocalTime());

                if (IsDryRun) continue;

                await CreateNotification(item);

                try
                {
                    DeleteItem(item.Item);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error deleting item: {name}", item.FullName);
                }
            }

            progress.Report(100);
        }

        private static bool FilterUsersList(List<string> users, UsersListMode mode, User x)
        {
            return users.Contains(x.Id.ToString("N")) switch
            {
                true when mode == UsersListMode.Ignore => false,
                true when mode == UsersListMode.Acknowledge => true,
                false when mode == UsersListMode.Ignore => true,
                false when mode == UsersListMode.Acknowledge => false,
                _ => throw new NotImplementedException(),
            };
        }

        private List<ExpiredItem> CollectMovies(List<User> users, List<User> usersWithFavorites, ItemsAdapter itemsAdapter, DateTime? startDate, CancellationToken cancellationToken)
        {
            var filters = new List<IExpiredItemFilter>
                {
                    new ExpiredFilter(_loggerFactory.CreateLogger<ExpiredFilter>(),
                        Configuration.KeepMoviesFor,
                        users.Count,
                        Configuration.KeepPlayedMovies),
                    new FavoritesFilter(_loggerFactory.CreateLogger<FavoritesFilter>(),
                        Configuration.KeepFavoriteMovies,
                        usersWithFavorites,
                        _userDataManager),
                    new LocationsFilter(_loggerFactory.CreateLogger<LocationsFilter>(),
                        Configuration.LocationsMode,
                        Configuration.LocationsExcluded,
                        _fileSystem)
                };

            AddTagFilterIfEnabled(filters);

            var moviesCollector = new MoviesJunkCollector(_loggerFactory.CreateLogger<MoviesJunkCollector>(), itemsAdapter);
            var expiredMovies = moviesCollector.Execute(users, filters, startDate, cancellationToken);
            return expiredMovies;
        }

        private IEnumerable<ExpiredItem> CollectNotPlayedMovies(List<User> users, List<User> usersWithFavorites, ItemsAdapter itemsAdapter, DateTime? startDate, CancellationToken cancellationToken)
        {
            var filters = new List<IExpiredItemFilter>
                {
                    new ExpiredNotPlayedFilter(_loggerFactory.CreateLogger<ExpiredNotPlayedFilter>(),
                        Configuration.KeepMoviesNotPlayedFor),
                    new FavoritesFilter(_loggerFactory.CreateLogger<FavoritesFilter>(),
                        Configuration.KeepFavoriteMovies,
                        usersWithFavorites,
                        _userDataManager),
                    new LocationsFilter(_loggerFactory.CreateLogger<LocationsFilter>(),
                        Configuration.LocationsMode,
                        Configuration.LocationsExcluded,
                        _fileSystem)
                };

            AddTagFilterIfEnabled(filters);

            var moviesCollector = new MoviesJunkCollector(_loggerFactory.CreateLogger<MoviesJunkCollector>(), itemsAdapter);
            var movies = moviesCollector.ExecuteNotPlayed(users, filters, startDate, cancellationToken);
            return movies;
        }

        private List<ExpiredItem> CollectSeries(List<User> users, List<User> usersWithFavorites, ItemsAdapter itemsAdapter, DateTime? startDate, CancellationToken cancellationToken)
        {
            var filters = new List<IExpiredItemFilter>
                {
                    new ExpiredFilter(_loggerFactory.CreateLogger<ExpiredFilter>(),
                        Configuration.KeepEpisodesFor,
                        users.Count,
                        Configuration.KeepPlayedEpisodes),
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

            AddTagFilterIfEnabled(filters);

            var seriesCollector = new SeriesJunkCollector(_loggerFactory.CreateLogger<SeriesJunkCollector>(), itemsAdapter);
            var expiredSeries = seriesCollector.Execute(users, filters, startDate, cancellationToken);
            return expiredSeries;
        }

        private IEnumerable<ExpiredItem> CollectNotPlayedSeries(List<User> users, List<User> usersWithFavorites, ItemsAdapter itemsAdapter, DateTime? startDate, CancellationToken cancellationToken)
        {
            var filters = new List<IExpiredItemFilter>
                {
                    new ExpiredNotPlayedFilter(_loggerFactory.CreateLogger<ExpiredNotPlayedFilter>(),
                        Configuration.KeepEpisodesNotPlayedFor),
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

            AddTagFilterIfEnabled(filters);

            var seriesCollector = new SeriesJunkCollector(_loggerFactory.CreateLogger<SeriesJunkCollector>(), itemsAdapter);
            var expiredSeries = seriesCollector.ExecuteNotPlayed(users, filters, startDate, cancellationToken);
            return expiredSeries;
        }

        private List<ExpiredItem> CollectVideos(List<User> users, List<User> usersWithFavorites, ItemsAdapter itemsAdapter, DateTime? startDate, CancellationToken cancellationToken)
        {
            var filters = new List<IExpiredItemFilter>
                {
                    new ExpiredFilter(_loggerFactory.CreateLogger<ExpiredFilter>(),
                        Configuration.KeepVideosFor,
                        users.Count,
                        Configuration.KeepPlayedVideos),
                    new FavoritesFilter(_loggerFactory.CreateLogger<FavoritesFilter>(),
                        Configuration.KeepFavoriteVideos,
                        usersWithFavorites,
                        _userDataManager),
                    new LocationsFilter(_loggerFactory.CreateLogger<LocationsFilter>(),
                        Configuration.LocationsMode,
                        Configuration.LocationsExcluded,
                        _fileSystem)
                };

            AddTagFilterIfEnabled(filters);

            var videosCollector = new VideosJunkCollector(_loggerFactory.CreateLogger<VideosJunkCollector>(), itemsAdapter);
            var expiredVideos = videosCollector.Execute(users, filters, startDate, cancellationToken);
            return expiredVideos;
        }

        private IEnumerable<ExpiredItem> CollectNotPlayedVideos(List<User> users, List<User> usersWithFavorites, ItemsAdapter itemsAdapter, DateTime? startDate, CancellationToken cancellationToken)
        {
            var filters = new List<IExpiredItemFilter>
                {
                    new ExpiredNotPlayedFilter(_loggerFactory.CreateLogger<ExpiredNotPlayedFilter>(),
                        Configuration.KeepVideosNotPlayedFor),
                    new FavoritesFilter(_loggerFactory.CreateLogger<FavoritesFilter>(),
                        Configuration.KeepFavoriteVideos,
                        usersWithFavorites,
                        _userDataManager),
                    new LocationsFilter(_loggerFactory.CreateLogger<LocationsFilter>(),
                        Configuration.LocationsMode,
                        Configuration.LocationsExcluded,
                        _fileSystem)
                };

            AddTagFilterIfEnabled(filters);

            var videosCollector = new VideosJunkCollector(_loggerFactory.CreateLogger<VideosJunkCollector>(), itemsAdapter);
            var expiredVideos = videosCollector.ExecuteNotPlayed(users, filters, startDate, cancellationToken);
            return expiredVideos;
        }

        private List<ExpiredItem> CollectAudio(List<User> users, List<User> usersWithFavorites, ItemsAdapter itemsAdapter, DateTime? startDate, CancellationToken cancellationToken)
        {
            var filters = new List<IExpiredItemFilter>
                {
                    new ExpiredFilter(_loggerFactory.CreateLogger<ExpiredFilter>(),
                        Configuration.KeepAudioFor,
                        users.Count,
                        Configuration.KeepPlayedAudio),
                    new FavoritesFilter(_loggerFactory.CreateLogger<FavoritesFilter>(),
                        Configuration.KeepFavoriteAudio,
                        usersWithFavorites,
                        _userDataManager),
                    new LocationsFilter(_loggerFactory.CreateLogger<LocationsFilter>(),
                        Configuration.LocationsMode,
                        Configuration.LocationsExcluded,
                        _fileSystem)
                };

            AddTagFilterIfEnabled(filters);

            var collector = new AudioJunkCollector(_loggerFactory.CreateLogger<AudioJunkCollector>(), itemsAdapter);
            var expiredItems = collector.Execute(users, filters, startDate, cancellationToken);
            return expiredItems;
        }

        private IEnumerable<ExpiredItem> CollectNotPlayedAudio(List<User> users, List<User> usersWithFavorites, ItemsAdapter itemsAdapter, DateTime? startDate, CancellationToken cancellationToken)
        {
            var filters = new List<IExpiredItemFilter>
                {
                    new ExpiredNotPlayedFilter(_loggerFactory.CreateLogger<ExpiredNotPlayedFilter>(),
                        Configuration.KeepAudioNotPlayedFor),
                    new FavoritesFilter(_loggerFactory.CreateLogger<FavoritesFilter>(),
                        Configuration.KeepFavoriteAudio,
                        usersWithFavorites,
                        _userDataManager),
                    new LocationsFilter(_loggerFactory.CreateLogger<LocationsFilter>(),
                        Configuration.LocationsMode,
                        Configuration.LocationsExcluded,
                        _fileSystem)
                };

            AddTagFilterIfEnabled(filters);

            var collector = new AudioJunkCollector(_loggerFactory.CreateLogger<AudioJunkCollector>(), itemsAdapter);
            var expiredItems = collector.ExecuteNotPlayed(users, filters, startDate, cancellationToken);
            return expiredItems;
        }

        private List<ExpiredItem> CollectAudioBook(List<User> users, List<User> usersWithFavorites, ItemsAdapter itemsAdapter, DateTime? startDate, CancellationToken cancellationToken)
        {
            var filters = new List<IExpiredItemFilter>
                {
                    new ExpiredFilter(_loggerFactory.CreateLogger<ExpiredFilter>(),
                        Configuration.KeepAudioBooksFor,
                        users.Count,
                        Configuration.KeepPlayedAudioBooks),
                    new FavoritesFilter(_loggerFactory.CreateLogger<FavoritesFilter>(),
                        Configuration.KeepFavoriteAudioBooks,
                        usersWithFavorites,
                        _userDataManager),
                    new LocationsFilter(_loggerFactory.CreateLogger<LocationsFilter>(),
                        Configuration.LocationsMode,
                        Configuration.LocationsExcluded,
                        _fileSystem)
                };

            AddTagFilterIfEnabled(filters);

            var collector = new AudioBookJunkCollector(_loggerFactory.CreateLogger<AudioBookJunkCollector>(), itemsAdapter);
            var expiredItems = collector.Execute(users, filters, startDate, cancellationToken);
            return expiredItems;
        }

        private IEnumerable<ExpiredItem> CollectNotPlayedAudioBook(List<User> users, List<User> usersWithFavorites, ItemsAdapter itemsAdapter, DateTime? startDate, CancellationToken cancellationToken)
        {
            var filters = new List<IExpiredItemFilter>
                {
                    new ExpiredNotPlayedFilter(_loggerFactory.CreateLogger<ExpiredNotPlayedFilter>(),
                        Configuration.KeepAudioBooksNotPlayedFor),
                    new FavoritesFilter(_loggerFactory.CreateLogger<FavoritesFilter>(),
                        Configuration.KeepFavoriteAudioBooks,
                        usersWithFavorites,
                        _userDataManager),
                    new LocationsFilter(_loggerFactory.CreateLogger<LocationsFilter>(),
                        Configuration.LocationsMode,
                        Configuration.LocationsExcluded,
                        _fileSystem)
                };

            AddTagFilterIfEnabled(filters);

            var collector = new AudioBookJunkCollector(_loggerFactory.CreateLogger<AudioBookJunkCollector>(), itemsAdapter);
            var expiredItems = collector.ExecuteNotPlayed(users, filters, startDate, cancellationToken);
            return expiredItems;
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
                    if (!(season.Series?.GetEpisodes().Any() ?? false) && !HasExtraFiles(season.Series))
                    {
                        _libraryManager.DeleteItem(season.Series!, opts, true);
                    }
                    break;

                case Episode episode:
                    _libraryManager.DeleteItem(episode, opts, true);
                    if (!episode.Season?.GetEpisodes().Any() ?? false)
                    {
                        _libraryManager.DeleteItem(episode.Season!, opts, true);
                    }
                    if (!(episode.Series?.GetEpisodes().Any() ?? false) && !HasExtraFiles(episode.Series))
                    {
                        _libraryManager.DeleteItem(episode.Series!, opts, true);
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
                    shortOverview = item.Kind switch
                    {
                        ExpiredKind.Played => $"Last played by {item.Data.First().User.Username} at {item.Data.First().LastPlayedDate.ToLocalTime()}",
                        ExpiredKind.NotPlayed => $"Not played by anyone since {item.Item.DateCreated.ToLocalTime()}",
                        _ => throw new NotImplementedException(),
                    };
                    overview = $"{movie.Path}";
                    break;

                case Series series:
                    title = $"\"{series.Name}\" was deleted";
                    shortOverview = item.Kind switch
                    {
                        ExpiredKind.Played => $"Last played by {item.Data.First().User.Username} at {item.Data.First().LastPlayedDate.ToLocalTime()}",
                        ExpiredKind.NotPlayed => $"Not played by anyone since {item.Item.DateCreated.ToLocalTime()}",
                        _ => throw new NotImplementedException(),
                    };
                    overview = $"{series.Path}";
                    break;

                case Season season:
                    title = $"\"{season.SeriesName}\" S{season.IndexNumber:D2} was deleted";
                    shortOverview = item.Kind switch
                    {
                        ExpiredKind.Played => $"Last played by {item.Data.First().User.Username} at {item.Data.First().LastPlayedDate.ToLocalTime()}",
                        ExpiredKind.NotPlayed => $"Not played by anyone since {item.Item.DateCreated.ToLocalTime()}",
                        _ => throw new NotImplementedException(),
                    };
                    overview = $"{season.Path ?? season.SeriesPath}";
                    break;

                case Episode episode:
                    title = $"\"{episode.SeriesName}\" S{episode.ParentIndexNumber:D2}E{episode.IndexNumber:D2} was deleted";
                    shortOverview = item.Kind switch
                    {
                        ExpiredKind.Played => $"Last played by {item.Data.First().User.Username} at {item.Data.First().LastPlayedDate.ToLocalTime()}",
                        ExpiredKind.NotPlayed => $"Not played by anyone since {item.Item.DateCreated.ToLocalTime()}",
                        _ => throw new NotImplementedException(),
                    };
                    overview = $"{episode.Path}";
                    break;

                case Video video:
                    title = $"\"{video.Name}\" was deleted";
                    shortOverview = item.Kind switch
                    {
                        ExpiredKind.Played => $"Last played by {item.Data.First().User.Username} at {item.Data.First().LastPlayedDate.ToLocalTime()}",
                        ExpiredKind.NotPlayed => $"Not played by anyone since {item.Item.DateCreated.ToLocalTime()}",
                        _ => throw new NotImplementedException(),
                    };
                    overview = $"{video.Path}";
                    break;

                default:
                    title = $"\"{item.Item.Name}\" was deleted";
                    shortOverview = item.Kind switch
                    {
                        ExpiredKind.Played => $"Last played by {item.Data.First().User.Username} at {item.Data.First().LastPlayedDate.ToLocalTime()}",
                        ExpiredKind.NotPlayed => $"Not played by anyone since {item.Item.DateCreated.ToLocalTime()}",
                        _ => throw new NotImplementedException(),
                    };
                    overview = $"{item.Item.Path}";
                    break;
            };

            await _activityManager.CreateAsync(new ActivityLog(title, "MediaCleaner", Guid.Empty)
            {
                ShortOverview = shortOverview,
                Overview = overview,
            });
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

        /// <summary>
        /// Adds a tag filter to the filter list if tag exclusion is enabled in the configuration.
        /// </summary>
        private void AddTagFilterIfEnabled(List<IExpiredItemFilter> filters)
        {
            if (Configuration.EnableTagExclusion)
            {
                _logger.LogDebug("Adding tag exclusion filter with tag: {ExclusionTag}", Configuration.ExclusionTag);
                filters.Add(
                    new TagFilter(
                        _loggerFactory.CreateLogger<TagFilter>(),
                        Configuration.ExclusionTag));
            }
        }
    }
}
