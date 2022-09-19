using System;
using System.Collections.Generic;
using System.Linq;
using MediaCleaner.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.IO;

namespace MediaCleaner.Filtering;

internal class LocationsFilter : IExpiredItemFilter
{
    private readonly LocationsListMode _mode;
    private readonly List<string> _locations;
    private readonly IFileSystem _fileSystem;

    public LocationsFilter(
        LocationsListMode mode,
        List<string> locations,
        IFileSystem fileSystem)
    {
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
        }

        return result;
    }
}
