using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaCleaner.Adapters;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.LeavingSoon;

public interface ILeavingSoonRefresher
{
    Task RefreshAsync(IProgress<double>? progress, CancellationToken cancellationToken);
}

public sealed class LeavingSoonRefreshService : ILeavingSoonRefresher
{
    private readonly ILogger<LeavingSoonRefreshService> _logger;
    private readonly IMediaCatalogAdapter _catalogAdapter;
    private readonly LeavingSoonCoordinator _coordinator;

    public LeavingSoonRefreshService(
        IUserManager userManager,
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        LeavingSoonCoordinator coordinator)
    {
        _logger = loggerFactory.CreateLogger<LeavingSoonRefreshService>();
        _catalogAdapter = new JellyfinMediaCatalogAdapter(
            loggerFactory.CreateLogger<JellyfinMediaCatalogAdapter>(),
            userManager,
            libraryManager,
            userDataManager);
        _coordinator = coordinator;
    }

    public async Task RefreshAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance!.Configuration;
        var policy = configuration.ToCleanupPolicy();
        var catalog = _catalogAdapter.Create(policy, cancellationToken);

        if (!configuration.LeavingSoon.Enabled)
        {
            await _coordinator.RefreshDisabledAsync(catalog, configuration.LeavingSoon, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Leaving Soon warnings and collection were cleared because the feature is disabled; personal protections remain active.");
            progress?.Report(100);
            return;
        }

        progress?.Report(50);
        await _coordinator.PrepareAsync(policy, catalog, configuration.LeavingSoon, cancellationToken).ConfigureAwait(false);
        progress?.Report(100);
    }
}
