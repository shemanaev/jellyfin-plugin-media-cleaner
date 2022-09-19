using System;
using System.Collections.Generic;

namespace MediaCleaner.Filtering;

internal class ExpiredFilter : IExpiredItemFilter
{
    private readonly int _keepFor;

    public ExpiredFilter(int keepFor)
    {
        _keepFor = keepFor;
    }

    public string Name => "Expired";

    public List<ExpiredItem> Apply(List<ExpiredItem> items)
    {
        var result = new List<ExpiredItem>();

        foreach (var item in items)
        {
            var expirationTime = item.LastPlayedDate.AddDays(_keepFor);
            if (DateTime.Now.CompareTo(expirationTime) < 0) continue;

            result.Add(item);
        }

        return result;
    }
}
