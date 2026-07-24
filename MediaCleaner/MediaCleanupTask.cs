using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private readonly string? _diagnosticRunId;

    public bool IsDryRun { get; init; }

    internal CleanupPlan? LastPlan { get; private set; }

    internal string DiagnosticStage { get; private set; } = "not started";

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
            userManager,
            loggerFactory,
            libraryManager,
            userDataManager,
            activityManager,
            localization,
            fileSystem,
            diagnosticRunId: null)
    {
    }

    internal MediaCleanupTask(
        IUserManager userManager,
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IActivityManager activityManager,
        ILocalizationManager localization,
        IFileSystem fileSystem,
        string? diagnosticRunId)
        : this(
            loggerFactory.CreateLogger<MediaCleanupTask>(),
            localization,
            new PluginCleanupPolicyProvider(),
            new JellyfinMediaCatalogAdapter(
                loggerFactory.CreateLogger<JellyfinMediaCatalogAdapter>(),
                userManager,
                libraryManager,
                userDataManager,
                diagnosticRunId: diagnosticRunId),
            new CleanupPlanner(new SystemClock(), new JellyfinPathMatcher(fileSystem), new JellyfinExtraFileProbe()),
            new JellyfinMutationAdapter(loggerFactory.CreateLogger<JellyfinMutationAdapter>(), libraryManager, activityManager),
            diagnosticRunId)
    {
    }

    internal MediaCleanupTask(
        ILogger<MediaCleanupTask> logger,
        ILocalizationManager localization,
        ICleanupPolicyProvider policyProvider,
        IMediaCatalogAdapter catalogAdapter,
        CleanupPlanner planner,
        IMediaMutationAdapter mutationAdapter,
        string? diagnosticRunId = null)
    {
        _logger = logger;
        _localization = localization;
        _policyProvider = policyProvider;
        _catalogAdapter = catalogAdapter;
        _planner = planner;
        _mutationAdapter = mutationAdapter;
        _diagnosticRunId = diagnosticRunId;
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();
        try
        {
            DiagnosticStage = "policy loading";
            var phaseStopwatch = LogDiagnosticPhaseStarted(DiagnosticStage);
            var policy = _policyProvider.GetPolicy();
            LogDiagnosticPhaseCompleted(DiagnosticStage, phaseStopwatch, "with {RuleCount} rules", policy.Rules.Count);
            _logger.LogDebug("Loaded {RuleCount} cleanup rules", policy.Rules.Count);

            DiagnosticStage = "catalog snapshot";
            phaseStopwatch = LogDiagnosticPhaseStarted(DiagnosticStage);
            var catalog = _catalogAdapter.Create(policy, cancellationToken);
            LogDiagnosticPhaseCompleted(
                DiagnosticStage,
                phaseStopwatch,
                "with {UsersCount} users and {ItemsCount} items",
                catalog.Users.Count,
                catalog.Items.Count);
            progress.Report(25);

            DiagnosticStage = "planner";
            phaseStopwatch = LogDiagnosticPhaseStarted(DiagnosticStage);
            var request = new CleanupRequest(policy, catalog.Users, catalog.Items, IsDryRun);
            var plan = _planner.Plan(request);
            LogDiagnosticPhaseCompleted(
                DiagnosticStage,
                phaseStopwatch,
                "with {DecisionCount} decisions and {AuditEntryCount} audit entries",
                plan.Decisions.Count,
                plan.AuditEntries.Count);
            LastPlan = IsDryRun ? plan : null;
            progress.Report(75);

            if (plan.Decisions.Count == 0)
            {
                _logger.LogInformation("No expired media found.");
                progress.Report(100);
                LogDiagnosticRunCompleted(totalStopwatch);
                return;
            }

            if (_policyProvider.RequiresMigrationReview)
            {
                _logger.LogWarning(
                    "Cleanup is paused because legacy settings were migrated to rules and must be reviewed and saved before deletion can run.");
                progress.Report(100);
                LogDiagnosticRunCompleted(totalStopwatch);
                return;
            }

            DiagnosticStage = "mutation";
            await _mutationAdapter.ExecuteAsync(plan, catalog, cancellationToken);
            progress.Report(100);
            LogDiagnosticRunCompleted(totalStopwatch);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_diagnosticRunId is not null)
            {
                _logger.LogWarning(
                    "Media Cleaner diagnostic run {DiagnosticRunId} canceled after {ElapsedMilliseconds} ms during {Stage}",
                    _diagnosticRunId,
                    totalStopwatch.ElapsedMilliseconds,
                    DiagnosticStage);
            }

            throw;
        }
    }

    private Stopwatch? LogDiagnosticPhaseStarted(string stage)
    {
        if (_diagnosticRunId is null)
        {
            return null;
        }

        _logger.LogInformation(
            "Media Cleaner diagnostic run {DiagnosticRunId}: {Stage} started",
            _diagnosticRunId,
            stage);
        return Stopwatch.StartNew();
    }

    private void LogDiagnosticPhaseCompleted(string stage, Stopwatch? stopwatch, string detailTemplate, params object?[] detailArguments)
    {
        if (_diagnosticRunId is null || stopwatch is null)
        {
            return;
        }

        var message = $"Media Cleaner diagnostic run {{DiagnosticRunId}}: {{Stage}} completed in {{ElapsedMilliseconds}} ms {detailTemplate}";
        var arguments = new object?[3 + detailArguments.Length];
        arguments[0] = _diagnosticRunId;
        arguments[1] = stage;
        arguments[2] = stopwatch.ElapsedMilliseconds;
        detailArguments.CopyTo(arguments, 3);
        _logger.LogInformation(message, arguments);
    }

    private void LogDiagnosticRunCompleted(Stopwatch stopwatch)
    {
        if (_diagnosticRunId is null)
        {
            return;
        }

        DiagnosticStage = "task completed";
        _logger.LogInformation(
            "Media Cleaner diagnostic run {DiagnosticRunId}: task completed in {ElapsedMilliseconds} ms",
            _diagnosticRunId,
            stopwatch.ElapsedMilliseconds);
    }
}
