using System;
using System.Collections.Generic;
using System.Linq;
using MediaCleaner.Configuration;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.Filtering;

internal class ExpiredFilter : IExpiredItemFilter
{
    private readonly ILogger<ExpiredFilter> _logger;
    private readonly int _keepFor;
    private readonly int _usersCount;
    private readonly PlayedKeepKind _keepKind;

    public ExpiredFilter(
        ILogger<ExpiredFilter> logger,
        int keepFor,
        int usersCount,
        PlayedKeepKind keepKind)
    {
        _logger = logger;
        _keepFor = keepFor;
        _usersCount = usersCount;
        _keepKind = keepKind;
    }

    public string Name => "Expired";

    public List<ExpiredItem> Apply(List<ExpiredItem> items)
    {
        var result = new List<ExpiredItem>();

        foreach (var item in items)
        {
            _logger.LogTrace("Filtering item \"{Name}\"", item.FullName);

            switch (_keepKind)
            {
                case PlayedKeepKind.AnyUser:
                    if (IsAnyUserWatched(item.Data))
                    {
                        result.Add(item);
                    }
                    break;

                case PlayedKeepKind.AnyUserRolling:
                    if (IsAnyUserRollingWatched(item.Data))
                    {
                        result.Add(item);
                    }
                    break;

                case PlayedKeepKind.AllUsers:
                    if (IsAllUsersWatched(item.Data))
                    {
                        result.Add(item);
                    }
                    break;
            }
        }

        return result;
    }

    private bool IsAnyUserWatched(List<ExpiredItemData> users)
    {
        foreach (var user in users)
        {
            if (!user.IsPlayed) continue;

            var expirationTime = user.LastPlayedDate.AddDays(_keepFor);
            if (DateTime.Now.CompareTo(expirationTime) < 0) continue;

            _logger.LogTrace("Played by \"{Username}\"", user.User.Username);
            return true;
        }

        return false;
    }

    private bool IsAnyUserRollingWatched(List<ExpiredItemData> users)
    {
        if (!users.Any(x => x.IsPlayed || x.IsWatching))
        {
            _logger.LogTrace("Not played by any user");
            return false;
        }

        var user = users.OrderByDescending(x => x.LastPlayedDate).First();
        var expirationTime = user.LastPlayedDate.AddDays(_keepFor);

        _logger.LogTrace("Latest played by \"{Username}\"", user.User.Username);
        return DateTime.Now.CompareTo(expirationTime) >= 0;
    }

    private bool IsAllUsersWatched(List<ExpiredItemData> users)
    {
        var expiredCount = 0;
        foreach (var user in users)
        {
            if (!user.IsPlayed) continue;

            var expirationTime = user.LastPlayedDate.AddDays(_keepFor);
            if (DateTime.Now.CompareTo(expirationTime) < 0) continue;

            expiredCount++;
        }

        return expiredCount >= _usersCount;
    }
}
