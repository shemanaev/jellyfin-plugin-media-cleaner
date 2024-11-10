using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.Logging;

internal class TroubleshootingLoggerProvider(TroubleshootingLoggerConfiguration config) : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, TroubleshootingLogger> _loggers =
        new(StringComparer.OrdinalIgnoreCase);

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new TroubleshootingLogger(name, GetCurrentConfig));

    private TroubleshootingLoggerConfiguration GetCurrentConfig() => config;

    public void Dispose()
    {
        _loggers.Clear();
    }
}
