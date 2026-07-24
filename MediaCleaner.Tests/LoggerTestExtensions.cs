using Microsoft.Extensions.Logging;
using Moq;

namespace MediaCleaner.Tests;

internal static class LoggerTestExtensions
{
    public static IReadOnlyList<string> Messages<T>(this Mock<ILogger<T>> logger) =>
        logger.Invocations
            .Where(invocation => invocation.Method.Name == nameof(ILogger.Log))
            .Select(invocation => invocation.Arguments[2]?.ToString() ?? string.Empty)
            .ToList();
}

internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull =>
        null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
