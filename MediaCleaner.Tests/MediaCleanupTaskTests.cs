using System.Reflection;
using MediaCleaner.Configuration;
using MediaCleaner.Integrations;
using Xunit;

namespace MediaCleaner.Tests;

public class MediaCleanupTaskTests
{
    [Fact]
    public void Task_does_not_cache_runtime_configuration_between_executions()
    {
        var instanceFields = typeof(MediaCleanupTask)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.DoesNotContain(instanceFields, field => field.FieldType == typeof(StructuredConfig));
        Assert.DoesNotContain(instanceFields, field => field.FieldType == typeof(ArrDeletionService));
    }
}
