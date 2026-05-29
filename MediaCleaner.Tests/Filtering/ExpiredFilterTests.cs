using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaCleaner.Configuration;
using MediaCleaner.Filtering;
using MediaCleaner.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaCleaner.Tests.Filtering;

public class ExpiredFilterTests
{
    [Fact]
    public void Any_user_rolling_keeps_items_currently_being_watched()
    {
        var filter = new ExpiredFilter(
            NullLogger<ExpiredFilter>.Instance,
            keepFor: 0,
            usersCount: 1,
            keepKind: PlayedKeepKind.AnyUserRolling);
        var item = new ExpiredItem
        {
            Item = new Movie { Name = "In progress" },
            Reason = ExpiredReason.Played,
            Data =
            [
                new ExpiredItemData
                {
                    User = new User("viewer", "default", "default"),
                    LastPlayedDate = DateTime.UtcNow.AddDays(-1),
                    IsPlayed = false,
                    IsWatching = true,
                }
            ],
        };

        var result = filter.Apply([item]);

        Assert.Empty(result);
    }
}
