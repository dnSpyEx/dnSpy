using Microsoft.Extensions.Logging;

namespace dnSpy.Extension.MalwareMCP;

internal sealed class FileLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName);
    public void Dispose() { }
}

internal sealed class FileLogger : ILogger
{
    private readonly string _category;
    public FileLogger(string category) { _category = category; }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var msg = formatter(state, exception);
        var line = $"[{logLevel.ToString().ToUpper()[..4]}] {_category}: {msg}";
        if (exception != null) line += $" EX: {exception}";
        Diagnostics.Log(line);
    }
}
