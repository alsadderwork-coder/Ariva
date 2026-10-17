using System.Text;
using System.Text.Json;

namespace Ariva.Simulation.Api.Security;

/// <summary>
/// <c>LogFile:Path</c>, as Ariva's hosts honour it (Ariva.Api.Common's ArivaLogging, which the simulator does not reference):
/// when set, every log line also goes to that file as one JSON object (Timestamp, Level, MessageTemplate, RenderedMessage,
/// Properties.SourceContext, Exception), so the E2E run's log scan (global-teardown.ts, CWE-532) reads the simulator's logs with
/// the hosts' and fails the run if a password, seed, code, key or token reached them. Microsoft.AspNetCore.Hosting below Warning
/// is left out of the file, as the hosts leave it out (its request line carries the query string). Off when the path is not set.
/// </summary>
internal sealed class SimulationLogFile : ILoggerProvider
{
    public const string PathKey = "LogFile:Path";

    #region Fields

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);
    private readonly Lock _gate = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    #endregion

    #region Constructors

    private SimulationLogFile(string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
        var stream = new FileStream(full, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
    }

    #endregion

    #region Methods

    /// <summary>Adds the file to the host's logging when <c>LogFile:Path</c> is set (the host disposes it at shutdown).</summary>
    public static void AddTo(ILoggingBuilder logging, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(logging);
        ArgumentNullException.ThrowIfNull(configuration);
        var path = configuration[PathKey];
        if (string.IsNullOrWhiteSpace(path))
            return;
        logging.Services.AddSingleton<ILoggerProvider>(_ => new SimulationLogFile(path));
        logging.AddFilter<SimulationLogFile>("Microsoft.AspNetCore.Hosting", LogLevel.Warning);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _writer.Dispose();
        }
    }

    private void Write(string line)
    {
        lock (_gate)
        {
            if (!_disposed)
                _writer.WriteLine(line);
        }
    }

    #endregion

    /// <summary>One category's logger: a JSON line per entry.</summary>
    private sealed class FileLogger(SimulationLogFile file, string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (!IsEnabled(logLevel))
                return;
            var template = state is IEnumerable<KeyValuePair<string, object>> values
                ? values.FirstOrDefault(v => v.Key == "{OriginalFormat}").Value as string
                : null;
            file.Write(JsonSerializer.Serialize(new
            {
                Timestamp = TimeProvider.System.GetUtcNow(),
                Level = logLevel.ToString(),
                MessageTemplate = template,
                RenderedMessage = formatter(state, exception),
                Properties = new { SourceContext = category, EventId = eventId.Id },
                Exception = exception?.ToString()
            }, Json));
        }
    }
}
