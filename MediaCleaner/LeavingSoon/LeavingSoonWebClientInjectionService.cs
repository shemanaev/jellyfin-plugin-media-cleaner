using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace MediaCleaner.LeavingSoon;

internal sealed class LeavingSoonWebClientInjectionService : BackgroundService
{
    private readonly LeavingSoonWebClientInstaller _installer;

    public LeavingSoonWebClientInjectionService(LeavingSoonWebClientInstaller installer)
    {
        _installer = installer;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _installer.Install();
        if (_installer.Status.Method is WebClientInjectionMethod.FileTransformation or WebClientInjectionMethod.JavaScriptInjector)
            return;

        for (var attempt = 0; attempt < 30 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            if (_installer.TryUpgradeToExternal())
                return;
        }
    }
}
