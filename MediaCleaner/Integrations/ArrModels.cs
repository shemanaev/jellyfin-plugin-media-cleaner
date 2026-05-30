using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MediaCleaner.Integrations;

public sealed record ArrConnectionResult(bool Success, string Message, string? Version = null);

internal enum ArrDeletionStatus
{
    Planned,
    Deleted,
    Skipped,
    Failed,
}

internal sealed record ArrDeletionResult(ArrDeletionStatus Status, string Message)
{
    public static ArrDeletionResult Planned(string message) => new(ArrDeletionStatus.Planned, message);

    public static ArrDeletionResult Deleted(string message) => new(ArrDeletionStatus.Deleted, message);

    public static ArrDeletionResult Skipped(string message) => new(ArrDeletionStatus.Skipped, message);

    public static ArrDeletionResult Failed(string message) => new(ArrDeletionStatus.Failed, message);
}

internal sealed record ArrSystemStatusResource
{
    [JsonPropertyName("version")]
    public string? Version { get; init; }
}

internal sealed record RadarrMovieResource
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("tmdbId")]
    public int? TmdbId { get; init; }

    [JsonPropertyName("imdbId")]
    public string? ImdbId { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("movieFile")]
    public RadarrMovieFileResource? MovieFile { get; init; }
}

internal sealed record RadarrMovieFileResource
{
    [JsonPropertyName("path")]
    public string? Path { get; init; }
}

internal sealed record SonarrSeriesResource
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("tvdbId")]
    public int? TvdbId { get; init; }

    [JsonPropertyName("imdbId")]
    public string? ImdbId { get; init; }

    [JsonPropertyName("tmdbId")]
    public int? TmdbId { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }
}

internal sealed record SonarrEpisodeResource
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("tvdbId")]
    public int? TvdbId { get; init; }

    [JsonPropertyName("seasonNumber")]
    public int SeasonNumber { get; init; }

    [JsonPropertyName("episodeNumber")]
    public int EpisodeNumber { get; init; }

    [JsonPropertyName("episodeFileId")]
    public int EpisodeFileId { get; init; }

    [JsonPropertyName("episodeFile")]
    public SonarrEpisodeFileResource? EpisodeFile { get; init; }
}

internal sealed record SonarrEpisodeFileResource
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }
}

internal sealed record SonarrEpisodesMonitoredRequest(IEnumerable<int> EpisodeIds, bool Monitored);
