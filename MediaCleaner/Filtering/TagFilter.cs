using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaCleaner.Configuration;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.Filtering;

internal class TagFilter : IExpiredItemFilter
{
    private readonly ILogger<TagFilter> _logger;
    private readonly string _tagName;
    private readonly TagMode _tagMode;

    public TagFilter(ILogger<TagFilter> logger, string tagName, TagMode tagMode)
    {
        _logger = logger;
        _tagName = tagName;
        _tagMode = tagMode;
    }

    public string Name => "Tag";

    public List<ExpiredItem> Apply(List<ExpiredItem> items)
    {
        var result = new List<ExpiredItem>();
        foreach (var item in items)
        {
            bool hasTag = HasTag(item.Item);
            if ((_tagMode == TagMode.Exclusion && hasTag) || 
                (_tagMode == TagMode.Inclusion && !hasTag))
            {
                string logReason = _tagMode == TagMode.Exclusion 
                    ? $"is tagged with \"{_tagName}\" (exclusion mode)"
                    : $"doesn't have the \"{_tagName}\" tag (inclusion mode)";
                _logger.LogTrace("\"{Name}\" {Reason}", item.FullName, logReason);
                continue;
            }
            result.Add(item);
        }
        return result;
    }

    private bool HasTag(BaseItem item)
    {
        if (item.Tags != null && item.Tags.Contains(_tagName))
        {
            return true;
        }
        if (item is Episode episode)
        {
            if (episode.Season?.Tags != null && episode.Season.Tags.Contains(_tagName))
            {
                return true;
            }
            if (episode.Series?.Tags != null && episode.Series.Tags.Contains(_tagName))
            {
                return true;
            }
        }
        return false;
    }
}
