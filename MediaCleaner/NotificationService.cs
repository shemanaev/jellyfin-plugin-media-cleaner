using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Activity;
using MediaCleaner.Models;

namespace MediaCleaner;

internal class NotificationService(IActivityManager activityManager)
{
    public async Task CreateNotification(ExpiredItem item)
    {
        string title;
        string shortOverview;
        string overview;

        switch (item.Item)
        {
            case Movie movie:
                title = $"\"{movie.Name}\" was deleted";
                shortOverview = item.Reason switch
                {
                    ExpiredReason.Played => $"Last played by {item.Data.First().User.Username} at {item.Data.First().LastPlayedDate.ToLocalTime()}",
                    ExpiredReason.NotPlayed => $"Not played by anyone since {item.Item.DateCreated.ToLocalTime()}",
                    _ => throw new NotImplementedException(),
                };
                overview = $"{movie.Path}";
                break;

            case Series series:
                title = $"\"{series.Name}\" was deleted";
                shortOverview = item.Reason switch
                {
                    ExpiredReason.Played => $"Last played by {item.Data.First().User.Username} at {item.Data.First().LastPlayedDate.ToLocalTime()}",
                    ExpiredReason.NotPlayed => $"Not played by anyone since {item.Item.DateCreated.ToLocalTime()}",
                    _ => throw new NotImplementedException(),
                };
                overview = $"{series.Path}";
                break;

            case Season season:
                title = $"\"{season.SeriesName}\" S{season.IndexNumber:D2} was deleted";
                shortOverview = item.Reason switch
                {
                    ExpiredReason.Played => $"Last played by {item.Data.First().User.Username} at {item.Data.First().LastPlayedDate.ToLocalTime()}",
                    ExpiredReason.NotPlayed => $"Not played by anyone since {item.Item.DateCreated.ToLocalTime()}",
                    _ => throw new NotImplementedException(),
                };
                overview = $"{season.Path ?? season.SeriesPath}";
                break;

            case Episode episode:
                title = $"\"{episode.SeriesName}\" S{episode.ParentIndexNumber:D2}E{episode.IndexNumber:D2} was deleted";
                shortOverview = item.Reason switch
                {
                    ExpiredReason.Played => $"Last played by {item.Data.First().User.Username} at {item.Data.First().LastPlayedDate.ToLocalTime()}",
                    ExpiredReason.NotPlayed => $"Not played by anyone since {item.Item.DateCreated.ToLocalTime()}",
                    _ => throw new NotImplementedException(),
                };
                overview = $"{episode.Path}";
                break;

            case Video video:
                title = $"\"{video.Name}\" was deleted";
                shortOverview = item.Reason switch
                {
                    ExpiredReason.Played => $"Last played by {item.Data.First().User.Username} at {item.Data.First().LastPlayedDate.ToLocalTime()}",
                    ExpiredReason.NotPlayed => $"Not played by anyone since {item.Item.DateCreated.ToLocalTime()}",
                    _ => throw new NotImplementedException(),
                };
                overview = $"{video.Path}";
                break;

            default:
                title = $"\"{item.Item.Name}\" was deleted";
                shortOverview = item.Reason switch
                {
                    ExpiredReason.Played => $"Last played by {item.Data.First().User.Username} at {item.Data.First().LastPlayedDate.ToLocalTime()}",
                    ExpiredReason.NotPlayed => $"Not played by anyone since {item.Item.DateCreated.ToLocalTime()}",
                    _ => throw new NotImplementedException(),
                };
                overview = $"{item.Item.Path}";
                break;
        }

        await activityManager.CreateAsync(new ActivityLog(title, "MediaCleaner", Guid.Empty)
        {
            ShortOverview = shortOverview,
            Overview = overview,
        });
    }
}
