using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.Filtering;

internal class TagFilter : IExpiredItemFilter
{
    private readonly ILogger<TagFilter> _logger;
    private readonly string _tagName;

    public TagFilter(ILogger<TagFilter> logger, string tagName)
    {
        _logger = logger;
        _tagName = tagName;
    }

    public string Name => "Tag";

    public List<ExpiredItem> Apply(List<ExpiredItem> items)
    {
        var result = new List<ExpiredItem>();
        foreach (var item in items)
        {
            if (ShouldKeepItem(item.Item))
            {
                _logger.LogTrace("\"{Name}\" is tagged with \"{TagName}\" or belongs to a tagged parent", item.FullName, _tagName);
                continue;
            }
            result.Add(item);
        }
        return result;
    }

    private bool ShouldKeepItem(BaseItem item)
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
