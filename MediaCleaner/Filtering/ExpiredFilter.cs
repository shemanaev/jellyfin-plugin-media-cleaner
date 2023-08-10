using System;
using System.Collections.Generic;
using MediaCleaner.Configuration;

namespace MediaCleaner.Filtering;

internal class ExpiredFilter : IExpiredItemFilter
{
    private readonly int _keepFor;
    private readonly int _usersCount;
    private readonly PlayedKeepKind _keepKind;

    public ExpiredFilter(int keepFor, int usersCount, PlayedKeepKind keepKind)
    {
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
            var expiredCount = 0;
            foreach (var user in item.Data)
            {
                var expirationTime = user.LastPlayedDate.AddDays(_keepFor);
                if (DateTime.Now.CompareTo(expirationTime) < 0) continue;
                expiredCount++;
            }

            if ((_keepKind == PlayedKeepKind.AnyUser && expiredCount > 0)
                || (_keepKind == PlayedKeepKind.AllUsers && expiredCount >= _usersCount))
            {
                result.Add(item);
            }
        }

        return result;
    }
}
