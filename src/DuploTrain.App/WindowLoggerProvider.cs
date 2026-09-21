using Microsoft.Extensions.Logging;

namespace DuploTrain.App;

/// <summary>Mirrors log output into the window.
///
/// A WinExe has no console attached, so without this the log would go nowhere
/// and the app would be undiagnosable in the field — which is exactly where its
/// faults show up.</summary>
public sealed class WindowLoggerProvider(TrainWindow window) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName)
        => new WindowLogger(window, ShortCategory(categoryName));

    /// <summary>"DuploTrain.Core.TrainRunner" reads as "TrainRunner" in a narrow
    /// window; the namespace is noise once you are looking at one app's log.</summary>
    private static string ShortCategory(string category)
    {
        var lastDot = category.LastIndexOf('.');
        return lastDot < 0 ? category : category[(lastDot + 1)..];
    }

    public void Dispose() { }

    private sealed class WindowLogger(TrainWindow window, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            if (exception is not null) message += $" - {exception.Message}";

            window.Append($"{DateTime.Now:HH:mm:ss} {Level(logLevel)} {category}: {message}");
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "none",
        };
    }
}
