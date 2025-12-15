using System.Collections.Generic;
using MediaCleaner.Models;

namespace MediaCleaner.Filtering;

internal interface IExpiredItemFilter
{
    string Name { get; }
    List<ExpiredItem> Apply(List<ExpiredItem> items);
}
