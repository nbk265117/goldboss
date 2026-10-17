using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DofusCompanion.Infrastructure.Logging;

public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message, string? Exception);

/// <summary>Keeps the last warnings/errors in memory so the UI (Diagnostics) can show them; errors are never only in files.</summary>
public sealed class InMemoryLogSink
{
    private const int Capacity = 200;
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public event EventHandler<LogEntry>? EntryAdded;

    public void Add(LogEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > Capacity && _entries.TryDequeue(out _)) { }
        EntryAdded?.Invoke(this, entry);
    }

    public IReadOnlyList<LogEntry> Snapshot() => _entries.ToArray();
}

/// <summary>
/// Minimal rolling file logger: one file per day, capped size, 7 days kept. Writes are serialized and
/// buffered by the OS; no screenshot content or OCR text is logged at Information level.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private readonly string _directory;
    private readonly InMemoryLogSink _sink;
    private readonly Lock _lock = new();
    private StreamWriter? _writer;
    private string? _currentPath;

    public FileLoggerProvider(string directory, InMemoryLogSink sink, LogLevel minimum)
    {
        _directory = directory;
        _sink = sink;
        MinimumLevel = minimum;
        Directory.CreateDirectory(directory);
        Cleanup();
    }

    public LogLevel MinimumLevel { get; set; }

    public string CurrentFile => Path.Combine(_directory, $"dofus-companion-{DateTime.Now:yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(LogEntry entry)
    {
        if (entry.Level >= LogLevel.Warning) _sink.Add(entry);

        var line = new StringBuilder()
            .Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(" [")
            .Append(entry.Level.ToString().ToUpperInvariant()[..4]).Append("] ")
            .Append(entry.Category).Append(": ").Append(entry.Message);
        if (entry.Exception is not null) line.AppendLine().Append(entry.Exception);

        lock (_lock)
        {
            try
            {
                var path = CurrentFile;
                if (_writer is null || _currentPath != path)
                {
                    _writer?.Dispose();
                    _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8) { AutoFlush = true };
                    _currentPath = path;
                }

                if (_writer.BaseStream.Length < MaxFileBytes)
                    _writer.WriteLine(line.ToString());
            }
            catch (IOException)
            {
                // Logging must never crash the app; the in-memory sink still has warnings and errors.
            }
        }
    }

    private void Cleanup()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_directory, "dofus-companion-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7)) File.Delete(f);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lock (_lock) _writer?.Dispose();
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider.MinimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(new LogEntry(DateTimeOffset.Now, logLevel, category, formatter(state, exception), exception?.ToString()));
        }
    }
}
