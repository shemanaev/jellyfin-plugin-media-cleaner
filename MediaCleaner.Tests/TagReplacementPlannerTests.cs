using FluentAssertions;
using MediaCleaner.Core;

namespace MediaCleaner.Tests;

public class TagReplacementPlannerTests
{
    [Fact]
    public void Plan_ReplacesOldTagCaseInsensitively_AndAvoidsDuplicateNewTag()
    {
        var items = new[]
        {
            new TagReplacementItem("one", ["Other", "KEEP"]),
            new TagReplacementItem("two", ["keep", "delete"]),
            new TagReplacementItem("three", null),
            new TagReplacementItem("four", ["other"]),
        };

        var plan = new TagReplacementPlanner().Plan("keep", "delete", items);

        plan.UpdatedCount.Should().Be(2);
        plan.SkippedCount.Should().Be(2);
        plan.Updates.Single(x => x.ItemId == "one").Tags.Should().Equal("Other", "delete");
        plan.Updates.Single(x => x.ItemId == "two").Tags.Should().Equal("delete");
    }
}
