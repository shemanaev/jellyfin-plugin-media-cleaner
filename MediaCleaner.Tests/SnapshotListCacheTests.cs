using FluentAssertions;
using MediaCleaner.Adapters;

namespace MediaCleaner.Tests;

public class SnapshotListCacheTests
{
    private const int ProgramCount = 72;
    private const int EpisodeCount = 3253;

    [Fact]
    public void GetOrAdd_CachesIssueScaleTvHierarchyLookupsByOwnerId()
    {
        var lookupCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var cache = new SnapshotListCache<Node>(node => node.Id, CancellationToken.None);

        for (var seriesIndex = 0; seriesIndex < ProgramCount; seriesIndex++)
        {
            var series = new Node($"series-{seriesIndex}");
            for (var episodeIndex = 0; episodeIndex < GetEpisodeCount(seriesIndex); episodeIndex++)
            {
                var items = cache.GetOrAdd(series, () =>
                {
                    lookupCounts[series.Id] = lookupCounts.GetValueOrDefault(series.Id) + 1;
                    return Enumerable.Range(0, GetEpisodeCount(seriesIndex))
                        .Select(x => new Node($"episode-{seriesIndex}-{x}"))
                        .ToList();
                });

                items.Should().HaveCount(GetEpisodeCount(seriesIndex));
            }
        }

        lookupCounts.Should().HaveCount(ProgramCount);
        lookupCounts.Values.Should().OnlyContain(x => x == 1);
        lookupCounts.Values.Sum().Should().Be(ProgramCount);
    }

    [Fact]
    public void GetOrAdd_ObservesCancellationBeforeLookup()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cache = new SnapshotListCache<Node>(node => node.Id, cts.Token);

        FluentActions.Invoking(() => cache.GetOrAdd(new Node("series"), () => []))
            .Should()
            .Throw<OperationCanceledException>();
    }

    private static int GetEpisodeCount(int seriesIndex) =>
        EpisodeCount / ProgramCount + (seriesIndex < EpisodeCount % ProgramCount ? 1 : 0);

    private sealed record Node(string Id);
}
