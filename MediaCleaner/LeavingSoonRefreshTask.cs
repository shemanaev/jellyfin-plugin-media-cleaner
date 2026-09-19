using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;
using MediaCleaner.Compatibility;
using MediaCleaner.LeavingSoon;

namespace MediaCleaner;

public sealed class LeavingSoonRefreshTask : IScheduledTask
{
    private readonly ILocalizationManager _localization;
    private readonly ILeavingSoonRefresher _refresher;

    public LeavingSoonRefreshTask(
        ILocalizationManager localization,
        ILeavingSoonRefresher refresher)
    {
        _localization = localization;
        _refresher = refresher;
    }

    public string Name => "Media Cleaner Leaving Soon refresh";
    public string Description => "Refresh the Leaving Soon collection without deleting media";
    public string Key => "MediaCleanerLeavingSoonRefresh";
    public string Category => _localization.GetLocalizedString("TasksMaintenanceCategory");

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        JellyfinCompatibility.CreateIntervalTrigger(TimeSpan.FromHours(6)),
    ];

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        _refresher.RefreshAsync(progress, cancellationToken);
}
