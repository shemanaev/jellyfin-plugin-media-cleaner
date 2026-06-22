using System;
using System.Collections.Generic;
using System.Linq;

namespace MediaCleaner.Core;

public sealed class TagReplacementPlanner
{
    public TagReplacementPlan Plan(string oldTag, string newTag, IReadOnlyList<TagReplacementItem> items)
    {
        var updates = new List<TagReplacementUpdate>();
        var skipped = 0;

        foreach (var item in items)
        {
            if (item.Tags is null)
            {
                skipped++;
                continue;
            }

            var tags = item.Tags.ToList();
            var exactOldTag = tags.FirstOrDefault(x => string.Equals(x, oldTag, StringComparison.OrdinalIgnoreCase));
            if (exactOldTag is null)
            {
                skipped++;
                continue;
            }

            tags.Remove(exactOldTag);
            if (!tags.Any(x => string.Equals(x, newTag, StringComparison.OrdinalIgnoreCase)))
            {
                tags.Add(newTag);
            }

            updates.Add(new TagReplacementUpdate(item.Id, tags));
        }

        return new TagReplacementPlan(oldTag, newTag, updates, updates.Count, skipped, 0, items.Count);
    }
}
