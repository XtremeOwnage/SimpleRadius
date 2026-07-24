namespace SimpleRadius.Services;

/// <summary>File logging options, bound from the "FileLogging" section of appsettings.json.</summary>
public class FileLoggingSettings
{
    public bool Enabled { get; set; }

    /// <summary>Directory for log files. Relative paths resolve against the content root.</summary>
    public string Directory { get; set; } = "logs";

    /// <summary>A new file is started once the current one passes this size.</summary>
    public int MaxFileSizeMegabytes { get; set; } = 10;

    /// <summary>How many rolled files to keep. Older ones are deleted.</summary>
    public int RetainedFileCount { get; set; } = 7;
}

/// <summary>
/// Console logging is always on and configured through the standard "Logging" section. This adds an
/// optional file sink so a systemd or container deployment can keep logs without an external collector.
/// </summary>
public static class LoggingSetup
{
    public static ILoggingBuilder AddSimpleRadiusFileLogging(
        this ILoggingBuilder builder,
        FileLoggingSettings settings,
        string contentRootPath)
    {
        if (!settings.Enabled)
        {
            return builder;
        }

        var directory = Path.IsPathRooted(settings.Directory)
            ? settings.Directory
            : Path.Combine(contentRootPath, settings.Directory);

        builder.AddProvider(new FileLoggerProvider(directory, settings));
        return builder;
    }
}

/// <summary>
/// A deliberately small rolling file logger. A dependency such as Serilog would bring more features, but
/// this keeps the deployment to a single binary with no extra configuration file to learn.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly FileLoggingSettings _settings;
    private StreamWriter? _writer;
    private string? _currentPath;

    public FileLoggerProvider(string directory, FileLoggingSettings settings)
    {
        _directory = directory;
        _settings = settings;
        System.IO.Directory.CreateDirectory(_directory);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                Roll();
                _writer?.WriteLine(line);
            }
            catch (IOException)
            {
                // Logging must never take the server down; the console sink still has the message.
            }
        }
    }

    private void Roll()
    {
        var path = Path.Combine(_directory, $"simpleradius-{DateTime.UtcNow:yyyyMMdd}.log");
        var tooBig = _currentPath == path
            && _writer is not null
            && _writer.BaseStream.Length > _settings.MaxFileSizeMegabytes * 1024L * 1024L;

        if (_writer is not null && _currentPath == path && !tooBig)
        {
            return;
        }

        _writer?.Dispose();

        if (tooBig)
        {
            path = Path.Combine(
                _directory,
                $"simpleradius-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        }

        _writer = new StreamWriter(path, append: true) { AutoFlush = true };
        _currentPath = path;

        Prune();
    }

    private void Prune()
    {
        var files = new DirectoryInfo(_directory)
            .GetFiles("simpleradius-*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(_settings.RetainedFileCount);

        foreach (var file in files)
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
                // Still in use; it will be caught by the next prune.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fffZ} [{Level(logLevel)}] {category}: {message}";

            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            provider.Write(line);
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "none"
        };
    }
}
