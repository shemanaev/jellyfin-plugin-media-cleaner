using System.Collections.Generic;

namespace MediaCleaner.Filtering;

internal interface IExpiredItemFilter
{
    string Name { get; }
    List<ExpiredItem> Apply(List<ExpiredItem> items);
}
