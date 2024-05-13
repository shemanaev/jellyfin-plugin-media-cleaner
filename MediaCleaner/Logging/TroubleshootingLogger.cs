using System;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.Logging;

internal class TroubleshootingLogger : ILogger
{
    private readonly string _name;
    private readonly Func<TroubleshootingLoggerConfiguration> _getCurrentConfig;

    public TroubleshootingLogger(
        string name,
        Func<TroubleshootingLoggerConfiguration> getCurrentConfig) =>
        (_name, _getCurrentConfig) = (string.Join('.', name.Split('.')[1..]), getCurrentConfig);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => default!;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var s = $"[{logLevel.ToString()[..3]}] {_name}: {formatter(state, exception)}";
        _getCurrentConfig().Output?.Add(s);
    }
}
