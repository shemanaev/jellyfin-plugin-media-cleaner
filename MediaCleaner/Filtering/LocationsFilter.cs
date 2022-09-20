using System;
using System.Collections.Generic;
using System.Linq;
using MediaCleaner.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.Filtering;

internal class LocationsFilter : IExpiredItemFilter
{
    private readonly ILogger<LocationsFilter> _logger;
    private readonly LocationsListMode _mode;
    private readonly List<string> _locations;
    private readonly IFileSystem _fileSystem;

    public LocationsFilter(
        ILogger<LocationsFilter> logger,
        LocationsListMode mode,
        List<string> locations,
        IFileSystem fileSystem)
    {
        _logger = logger;
        _mode = mode;
        _locations = locations;
        _fileSystem = fileSystem;
    }

    public string Name => "Locations";

    public List<ExpiredItem> Apply(List<ExpiredItem> items)
    {
        var result = new List<ExpiredItem>();

        foreach (var item in items)
        {
            var path = item.Item switch
            {
                Episode episode => episode.Path,
                Season season => season.GetEpisodes().Where(x => !x.IsVirtualItem).First().Path,
                Series series => series.GetEpisodes().Where(x => !x.IsVirtualItem).First().Path,
                Movie movie => movie.Path,
                _ => item.Item.Path
            };

            var contains = _locations.Any(s => _fileSystem.ContainsSubPath(s, path));
            var shouldDelete = _mode switch
            {
                LocationsListMode.Exclude => !contains,
                LocationsListMode.Include => contains,
                _ => throw new NotSupportedException()
            };

            if (shouldDelete)
            {
                result.Add(item);
            }
            else
            {
                var location = _locations.Find(s => _fileSystem.ContainsSubPath(s, path));
                _logger.LogTrace("\"{Name}\" is belongs to \"{Location}\"", item.Item.Name, location);
            }
        }

        return result;
    }
}
