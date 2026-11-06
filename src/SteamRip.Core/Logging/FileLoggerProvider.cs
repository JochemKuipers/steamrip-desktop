using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SteamRip.Core.Logging;

/// <summary>
/// Plain-text daily log files: <c>&lt;dir&gt;/steamrip-YYYYMMDD.log</c>, one line per entry, older files pruned on roll.
/// Also fans every formatted line out to <see cref="LineWritten"/> so the UI can show a live tail without re-reading the file.
/// Writes are serialised on a background thread so logging never blocks the caller.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly int _keepDays;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>());
    private readonly Thread _writer;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private StreamWriter? _current;
    private DateOnly _currentDay;

    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
    public string Directory => _dir;
    public event Action<string>? LineWritten;

    public FileLoggerProvider(string dir, int keepDays = 7)
    {
        _dir = dir; _keepDays = keepDays;
        System.IO.Directory.CreateDirectory(dir);
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "steamrip-log" };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, n => new FileLogger(this, Shorten(n)));

    private static string Shorten(string category)
    {
        // "SteamRip.Core.Hosters.FileDitchResolver" → "FileDitchResolver"
        var i = category.LastIndexOf('.');
        return i >= 0 ? category[(i + 1)..] : category;
    }

    internal void Enqueue(LogLevel level, string category, string message, Exception? ex)
    {
        var sb = new StringBuilder(message.Length + 64);
        sb.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(' ').Append(Abbrev(level)).Append(" [").Append(category).Append("] ").Append(message);
        if (ex is not null) sb.AppendLine().Append(ex);
        var line = sb.ToString();
        if (!_queue.IsAddingCompleted) _queue.Add(line);
        LineWritten?.Invoke(line);
    }

    private static string Abbrev(LogLevel l) => l switch
    {
        LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
        LogLevel.Warning => "WRN", LogLevel.Error => "ERR", LogLevel.Critical => "CRT", _ => "???",
    };

    private void WriterLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (_current is null || today != _currentDay) Roll(today);
                _current!.WriteLine(line);
                _current.Flush();
            }
            catch { /* a failing log must never take the app down */ }
        }
        _current?.Dispose();
    }

    private void Roll(DateOnly day)
    {
        _current?.Dispose();
        _currentDay = day;
        _current = new StreamWriter(new FileStream(Path.Combine(_dir, $"steamrip-{day:yyyyMMdd}.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
        try
        {
            foreach (var f in System.IO.Directory.EnumerateFiles(_dir, "steamrip-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-_keepDays)) File.Delete(f);
        }
        catch { }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= owner.MinimumLevel;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            owner.Enqueue(logLevel, category, formatter(state, exception), exception);
        }
        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }
}
