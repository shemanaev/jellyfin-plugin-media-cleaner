using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaCleaner.Configuration;
using MediaCleaner.Models;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.Integrations;

internal sealed class ArrDeletionService(
    ILogger<ArrDeletionService> logger,
    RadarrClient radarrClient,
    SonarrClient sonarrClient)
{
    public static ArrDeletionService Create(ILoggerFactory loggerFactory, ConfigArrNode config) =>
        new(
            loggerFactory.CreateLogger<ArrDeletionService>(),
            new RadarrClient(new HttpClient(), config.Radarr),
            new SonarrClient(new HttpClient(), config.Sonarr));

    public async Task<ArrDeletionResult> DeleteAsync(
        ExpiredItem item,
        bool isDryRun,
        CancellationToken cancellationToken)
    {
        try
        {
            return item.Item switch
            {
                Movie movie => await DeleteMovieAsync(movie, isDryRun, cancellationToken),
                Series series => await DeleteSeriesAsync(series, isDryRun, cancellationToken),
                Season season => await DeleteSeasonAsync(season, isDryRun, cancellationToken),
                Episode episode => await DeleteEpisodeAsync(episode, isDryRun, cancellationToken),
                _ => ArrDeletionResult.Skipped(
                    $"{item.FullName} is {item.Item.GetType().Name}; Radarr/Sonarr cannot manage this media type."),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogError(ex, "Arr deletion failed for {Name}", item.FullName);
            return ArrDeletionResult.Failed($"Arr deletion failed for {item.FullName}: {ex.Message}");
        }
    }

    private async Task<ArrDeletionResult> DeleteMovieAsync(
        Movie movie,
        bool isDryRun,
        CancellationToken cancellationToken)
    {
        if (!radarrClient.IsConfigured)
        {
            return ArrDeletionResult.Skipped($"Radarr is not configured; skipping movie \"{movie.Name}\".");
        }

        var radarrMovie = await FindRadarrMovieAsync(movie, cancellationToken);
        if (radarrMovie is null)
        {
            return ArrDeletionResult.Skipped($"No Radarr match found for movie \"{movie.Name}\".");
        }

        var message = $"Radarr movie {radarrMovie.Id} matched for \"{movie.Name}\".";
        if (isDryRun)
        {
            return ArrDeletionResult.Planned($"Dry run: would delete {message}");
        }

        await radarrClient.DeleteMovieAsync(radarrMovie.Id, cancellationToken);
        return ArrDeletionResult.Deleted($"Deleted {message}");
    }

    private async Task<ArrDeletionResult> DeleteSeriesAsync(
        Series series,
        bool isDryRun,
        CancellationToken cancellationToken)
    {
        if (!sonarrClient.IsConfigured)
        {
            return ArrDeletionResult.Skipped($"Sonarr is not configured; skipping series \"{series.Name}\".");
        }

        var sonarrSeries = await FindSonarrSeriesAsync(series, cancellationToken);
        if (sonarrSeries is null)
        {
            return ArrDeletionResult.Skipped($"No Sonarr match found for series \"{series.Name}\".");
        }

        var message = $"Sonarr series {sonarrSeries.Id} matched for \"{series.Name}\".";
        if (isDryRun)
        {
            return ArrDeletionResult.Planned($"Dry run: would delete {message}");
        }

        await sonarrClient.DeleteSeriesAsync(sonarrSeries.Id, cancellationToken);
        return ArrDeletionResult.Deleted($"Deleted {message}");
    }

    private async Task<ArrDeletionResult> DeleteSeasonAsync(
        Season season,
        bool isDryRun,
        CancellationToken cancellationToken)
    {
        if (!sonarrClient.IsConfigured)
        {
            return ArrDeletionResult.Skipped($"Sonarr is not configured; skipping season \"{season.Name}\".");
        }

        if (season.Series is null)
        {
            return ArrDeletionResult.Skipped($"Season \"{season.Name}\" has no parent series.");
        }

        var sonarrSeries = await FindSonarrSeriesAsync(season.Series, cancellationToken);
        if (sonarrSeries is null)
        {
            return ArrDeletionResult.Skipped($"No Sonarr match found for series \"{season.SeriesName}\".");
        }

        var episodes = await sonarrClient.GetEpisodesAsync(sonarrSeries.Id, season.IndexNumber, cancellationToken);
        var episodeIds = episodes.Select(x => x.Id).Where(x => x > 0).Distinct().ToList();
        var fileIds = episodes
            .Select(x => x.EpisodeFile?.Id > 0 ? x.EpisodeFile.Id : x.EpisodeFileId)
            .Where(x => x > 0)
            .Distinct()
            .ToList();

        if (episodeIds.Count == 0 || fileIds.Count == 0)
        {
            return ArrDeletionResult.Skipped($"No Sonarr episode files found for season \"{season.Name}\".");
        }

        var message = $"Sonarr season {season.IndexNumber} in series {sonarrSeries.Id} with {fileIds.Count} file(s).";
        if (isDryRun)
        {
            return ArrDeletionResult.Planned($"Dry run: would unmonitor and delete {message}");
        }

        await sonarrClient.SetEpisodesMonitoredAsync(episodeIds, false, cancellationToken);
        foreach (var fileId in fileIds)
        {
            await sonarrClient.DeleteEpisodeFileAsync(fileId, cancellationToken);
        }

        return ArrDeletionResult.Deleted($"Deleted {message}");
    }

    private async Task<ArrDeletionResult> DeleteEpisodeAsync(
        Episode episode,
        bool isDryRun,
        CancellationToken cancellationToken)
    {
        if (!sonarrClient.IsConfigured)
        {
            return ArrDeletionResult.Skipped($"Sonarr is not configured; skipping episode \"{episode.Name}\".");
        }

        if (episode.Series is null)
        {
            return ArrDeletionResult.Skipped($"Episode \"{episode.Name}\" has no parent series.");
        }

        var sonarrSeries = await FindSonarrSeriesAsync(episode.Series, cancellationToken);
        if (sonarrSeries is null)
        {
            return ArrDeletionResult.Skipped($"No Sonarr match found for series \"{episode.SeriesName}\".");
        }

        var episodes = await sonarrClient.GetEpisodesAsync(sonarrSeries.Id, episode.ParentIndexNumber, cancellationToken);
        var sonarrEpisode = FindSonarrEpisode(episode, episodes);
        if (sonarrEpisode is null)
        {
            return ArrDeletionResult.Skipped($"No Sonarr match found for episode \"{episode.Name}\".");
        }

        var fileId = sonarrEpisode.EpisodeFile?.Id > 0 ? sonarrEpisode.EpisodeFile.Id : sonarrEpisode.EpisodeFileId;
        if (fileId <= 0)
        {
            return ArrDeletionResult.Skipped($"No Sonarr episode file found for episode \"{episode.Name}\".");
        }

        var message = $"Sonarr episode {sonarrEpisode.Id} file {fileId} for \"{episode.Name}\".";
        if (isDryRun)
        {
            return ArrDeletionResult.Planned($"Dry run: would unmonitor and delete {message}");
        }

        await sonarrClient.SetEpisodesMonitoredAsync([sonarrEpisode.Id], false, cancellationToken);
        await sonarrClient.DeleteEpisodeFileAsync(fileId, cancellationToken);
        return ArrDeletionResult.Deleted($"Deleted {message}");
    }

    private async Task<RadarrMovieResource?> FindRadarrMovieAsync(
        Movie movie,
        CancellationToken cancellationToken)
    {
        var tmdbId = GetProviderInt(movie, "Tmdb");
        if (tmdbId.HasValue)
        {
            var matches = await radarrClient.GetMoviesByTmdbIdAsync(tmdbId.Value, cancellationToken);
            var match = matches.FirstOrDefault();
            if (match is not null)
            {
                return match;
            }
        }

        var imdbId = GetProviderId(movie, "Imdb");
        var movies = await radarrClient.GetMoviesAsync(cancellationToken);
        return movies.FirstOrDefault(x => ProviderMatches(x.ImdbId, imdbId))
               ?? movies.FirstOrDefault(x => PathMatches(movie.Path, x.Path, x.MovieFile?.Path));
    }

    private async Task<SonarrSeriesResource?> FindSonarrSeriesAsync(
        Series series,
        CancellationToken cancellationToken)
    {
        var tvdbId = GetProviderInt(series, "Tvdb");
        if (tvdbId.HasValue)
        {
            var matches = await sonarrClient.GetSeriesByTvdbIdAsync(tvdbId.Value, cancellationToken);
            var match = matches.FirstOrDefault();
            if (match is not null)
            {
                return match;
            }
        }

        var imdbId = GetProviderId(series, "Imdb");
        var tmdbId = GetProviderInt(series, "Tmdb");
        var seriesList = await sonarrClient.GetSeriesAsync(cancellationToken);
        return seriesList.FirstOrDefault(x => ProviderMatches(x.ImdbId, imdbId))
               ?? seriesList.FirstOrDefault(x => tmdbId.HasValue && x.TmdbId == tmdbId)
               ?? seriesList.FirstOrDefault(x => PathMatches(series.Path, x.Path, null));
    }

    private static SonarrEpisodeResource? FindSonarrEpisode(
        Episode episode,
        IReadOnlyList<SonarrEpisodeResource> episodes)
    {
        var tvdbId = GetProviderInt(episode, "Tvdb");
        return episodes.FirstOrDefault(x => tvdbId.HasValue && x.TvdbId == tvdbId)
               ?? episodes.FirstOrDefault(x =>
                   x.SeasonNumber == episode.ParentIndexNumber
                   && x.EpisodeNumber == episode.IndexNumber)
               ?? episodes.FirstOrDefault(x => PathMatches(episode.Path, x.EpisodeFile?.Path, null));
    }

    private static string? GetProviderId(BaseItem item, string key)
    {
        if (item.ProviderIds is null)
        {
            return null;
        }

        return item.ProviderIds.TryGetValue(key, out var value)
            ? value
            : item.ProviderIds.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static int? GetProviderInt(BaseItem item, string key) =>
        int.TryParse(GetProviderId(item, key), out var value) ? value : null;

    private static bool ProviderMatches(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool PathMatches(string? jellyfinPath, string? arrPath, string? arrFilePath)
    {
        if (string.IsNullOrWhiteSpace(jellyfinPath))
        {
            return false;
        }

        return PathsEqual(jellyfinPath, arrFilePath)
               || PathsEqual(jellyfinPath, arrPath)
               || IsPathInside(jellyfinPath, arrPath);
    }

    private static bool PathsEqual(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(NormalisePath(left), NormalisePath(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsPathInside(string child, string? parent)
    {
        if (string.IsNullOrWhiteSpace(parent))
        {
            return false;
        }

        var normalisedChild = NormalisePath(child);
        var normalisedParent = NormalisePath(parent);
        return normalisedChild.StartsWith(normalisedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || normalisedChild.StartsWith(normalisedParent + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalisePath(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
