using MediaCleaner.Core;

namespace MediaCleaner.Adapters;

internal sealed class PluginCleanupPolicyProvider : ICleanupPolicyProvider
{
    public CleanupPolicy GetPolicy() => Plugin.Instance!.Configuration.ToCleanupPolicy();

    public bool RequiresMigrationReview => Plugin.Instance!.Configuration.RequiresMigrationReview();
}
