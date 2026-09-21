using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaCleaner.Adapters;
using MediaCleaner.Core;
using MediaCleaner.LeavingSoon;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace MediaCleaner;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<LeavingSoonWebClientInstaller>();
        serviceCollection.AddSingleton<IStartupFilter, LeavingSoonWebClientStartupFilter>();
        serviceCollection.AddHostedService<LeavingSoonWebClientInjectionService>();
        serviceCollection.AddSingleton<ILeavingSoonStateStore>(services =>
        {
            var paths = services.GetRequiredService<IApplicationPaths>();
            var databasePath = Path.Combine(paths.PluginConfigurationsPath, "MediaCleaner", "state-v1.db");
            return new SqliteLeavingSoonStateStore(
                databasePath,
                services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SqliteLeavingSoonStateStore>>());
        });
        serviceCollection.AddSingleton<ILeavingSoonCollectionPublisher, JellyfinLeavingSoonCollectionPublisher>();
        serviceCollection.AddSingleton<ILeavingSoonPlaybackReader, JellyfinLeavingSoonPlaybackReader>();
        serviceCollection.AddSingleton<LeavingSoonCoordinator>(services =>
        {
            var fileSystem = services.GetRequiredService<MediaBrowser.Model.IO.IFileSystem>();
            var planner = new CleanupPlanner(new SystemClock(), new JellyfinPathMatcher(fileSystem), new JellyfinExtraFileProbe());
            return new LeavingSoonCoordinator(
                services.GetRequiredService<ILeavingSoonStateStore>(),
                services.GetRequiredService<ILeavingSoonCollectionPublisher>(),
                services.GetRequiredService<ILeavingSoonPlaybackReader>(),
                planner,
                new SystemClock(),
                services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LeavingSoonCoordinator>>());
        });
        serviceCollection.AddSingleton<ILeavingSoonRefresher, LeavingSoonRefreshService>();
    }
}
