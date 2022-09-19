using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.Logging;

internal class TroubleshootingLoggerProvider : ILoggerProvider
{
    private readonly TroubleshootingLoggerConfiguration _currentConfig;
    private readonly ConcurrentDictionary<string, TroubleshootingLogger> _loggers =
        new(StringComparer.OrdinalIgnoreCase);

    public TroubleshootingLoggerProvider(TroubleshootingLoggerConfiguration config)
    {
        _currentConfig = config;
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new TroubleshootingLogger(name, GetCurrentConfig));

    private TroubleshootingLoggerConfiguration GetCurrentConfig() => _currentConfig;

    public void Dispose()
    {
        _loggers.Clear();
    }
}
