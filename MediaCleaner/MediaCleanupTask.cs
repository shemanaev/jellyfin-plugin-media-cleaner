using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using MediaCleaner.Adapters;
using MediaCleaner.Compatibility;
using MediaCleaner.Core;
using Microsoft.Extensions.Logging;

namespace MediaCleaner;

public class MediaCleanupTask : IScheduledTask
{
    private readonly ILogger<MediaCleanupTask> _logger;
    private readonly ILocalizationManager _localization;
    private readonly ICleanupPolicyProvider _policyProvider;
    private readonly IMediaCatalogAdapter _catalogAdapter;
    private readonly CleanupPlanner _planner;
    private readonly IMediaMutationAdapter _mutationAdapter;

    public bool IsDryRun { get; init; }

    internal CleanupPlan? LastPlan { get; private set; }

    public string Name => "Media Cleaner cleanup";

    public string Description => "Delete played media files according to specified rules";

    public string Key => "MediaCleanup";

    public string Category => _localization.GetLocalizedString("TasksMaintenanceCategory");

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        JellyfinCompatibility.CreateIntervalTrigger(TimeSpan.FromDays(1)),
    ];

    public MediaCleanupTask(
        IUserManager userManager,
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IActivityManager activityManager,
        ILocalizationManager localization,
        IFileSystem fileSystem)
        : this(
            loggerFactory.CreateLogger<MediaCleanupTask>(),
            localization,
            new PluginCleanupPolicyProvider(),
            new JellyfinMediaCatalogAdapter(
                loggerFactory.CreateLogger<JellyfinMediaCatalogAdapter>(),
                userManager,
                libraryManager,
                userDataManager),
            new CleanupPlanner(new SystemClock(), new JellyfinPathMatcher(fileSystem), new JellyfinExtraFileProbe()),
            new JellyfinMutationAdapter(loggerFactory.CreateLogger<JellyfinMutationAdapter>(), libraryManager, activityManager))
    {
    }

    internal MediaCleanupTask(
        ILogger<MediaCleanupTask> logger,
        ILocalizationManager localization,
        ICleanupPolicyProvider policyProvider,
        IMediaCatalogAdapter catalogAdapter,
        CleanupPlanner planner,
        IMediaMutationAdapter mutationAdapter)
    {
        _logger = logger;
        _localization = localization;
        _policyProvider = policyProvider;
        _catalogAdapter = catalogAdapter;
        _planner = planner;
        _mutationAdapter = mutationAdapter;
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var policy = _policyProvider.GetPolicy();
        _logger.LogDebug("Loaded {RuleCount} cleanup rules", policy.Rules.Count);

        var catalog = _catalogAdapter.Create(policy, cancellationToken);
        progress.Report(25);

        var request = new CleanupRequest(policy, catalog.Users, catalog.Items, IsDryRun);
        var plan = _planner.Plan(request);
        LastPlan = plan;
        progress.Report(75);

        if (plan.Decisions.Count == 0)
        {
            _logger.LogInformation("No expired media found.");
            progress.Report(100);
            return;
        }

        if (_policyProvider.RequiresMigrationReview)
        {
            _logger.LogWarning(
                "Cleanup is paused because legacy settings were migrated to rules and must be reviewed and saved before deletion can run.");
            progress.Report(100);
            return;
        }

        await _mutationAdapter.ExecuteAsync(plan, catalog, cancellationToken);
        progress.Report(100);
    }
}
