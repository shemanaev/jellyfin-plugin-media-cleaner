using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.Filtering;

internal class ExpiredNotPlayedFilter : IExpiredItemFilter
{
    private readonly ILogger<ExpiredNotPlayedFilter> _logger;
    private readonly int _keepFor;

    public ExpiredNotPlayedFilter(
        ILogger<ExpiredNotPlayedFilter> logger,
        int keepFor)
    {
        _logger = logger;
        _keepFor = keepFor;
    }

    public string Name => "ExpiredNotPlayed";

    public List<ExpiredItem> Apply(List<ExpiredItem> items)
    {
        var result = new List<ExpiredItem>();

        foreach (var item in items)
        {
            _logger.LogTrace("Filtering item \"{Name}\"", item.FullName);

            var expirationTime = item.Item.DateCreated.AddDays(_keepFor);
            if (DateTime.UtcNow.CompareTo(expirationTime) < 0) continue;

            _logger.LogTrace("Not played by anyone since {DateAdded}", item.Item.DateCreated.ToLocalTime());
            result.Add(item);
        }

        return result;
    }
}
