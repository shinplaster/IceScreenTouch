using System.Globalization;
using System.Text;

namespace IceScreenTouch.App.Logging;

/// <summary>
/// The app log: every line goes to a file in the log folder (one per run) and to a short in-memory list
/// for the log view. Old files are deleted so that the folder does not grow without end. Thread-safe.
/// </summary>
public sealed class AppLog : IDisposable
{
    /// <summary>How many log files of each kind are kept.</summary>
    public const int KeepFiles = 10;

    /// <summary>How many lines the in-memory list keeps.</summary>
    public const int RecentLines = 500;

    private readonly Lock _lock = new();
    private readonly Queue<string> _recent = new();
    private readonly TimeProvider _time;
    private StreamWriter? _file;

    /// <summary>
    /// Opens a new log file in <paramref name="directory"/> (none if null). If the file cannot be created,
    /// the log still works in memory and <see cref="FilePath"/> is null.
    /// </summary>
    public AppLog(string? directory, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        Directory = directory;
        if (directory is null)
        {
            return;
        }

        try
        {
            var path = NewFilePath("IceScreenTouch");
            _file = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
            FilePath = path;
            Prune(directory, "IceScreenTouch-*.log", KeepFiles);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _file = null;
            Write($"[log ] cannot write the log file in {directory}: {ex.Message}");
        }
    }

    /// <summary>Raised (on the writing thread) for every line, with its timestamp.</summary>
    public event EventHandler<string>? LineAdded;

    /// <summary>The log folder, or null for a memory-only log.</summary>
    public string? Directory { get; }

    /// <summary>The current log file, or null.</summary>
    public string? FilePath { get; }

    /// <summary>
    /// A new timestamped file name in the log folder, e.g. uxplay-20261105-213000.log; creates the folder and
    /// deletes the oldest files with the same prefix beyond <see cref="KeepFiles"/>. Null for a memory-only log.
    /// </summary>
    public string? NewLogFile(string prefix)
    {
        if (Directory is null)
        {
            return null;
        }

        try
        {
            var path = NewFilePath(prefix);
            Prune(Directory, prefix + "-*.log", KeepFiles - 1);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Write($"[log ] cannot create a {prefix} log in {Directory}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Writes a line.</summary>
    public void Write(string message)
    {
        var line = $"{_time.GetLocalNow():HH:mm:ss.fff} {message}";
        lock (_lock)
        {
            _recent.Enqueue(line);
            while (_recent.Count > RecentLines)
            {
                _recent.Dequeue();
            }

            try
            {
                _file?.WriteLine(_time.GetLocalNow().ToString("yyyy-MM-dd ", CultureInfo.InvariantCulture) + line);
            }
            catch (IOException)
            {
                // Disk full or similar; keep the in-memory log going.
            }
        }

        LineAdded?.Invoke(this, line);
    }

    /// <summary>The latest lines, oldest first.</summary>
    public IReadOnlyList<string> Recent()
    {
        lock (_lock)
        {
            return [.. _recent];
        }
    }

    /// <summary>Closes the file.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _file?.Dispose();
            _file = null;
        }
    }

    /// <summary>Deletes the oldest files matching <paramref name="pattern"/> so that at most <paramref name="keep"/> remain.</summary>
    public static void Prune(string directory, string pattern, int keep)
    {
        if (!System.IO.Directory.Exists(directory))
        {
            return;
        }

        var old = new DirectoryInfo(directory).GetFiles(pattern)
            .OrderByDescending(f => f.Name, StringComparer.Ordinal) // timestamped names sort by time
            .Skip(Math.Max(0, keep));
        foreach (var f in old)
        {
            try
            {
                f.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use (another instance); try again next time.
            }
        }
    }

    private string NewFilePath(string prefix)
    {
        System.IO.Directory.CreateDirectory(Directory!);
        var stamp = _time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(Directory!, $"{prefix}-{stamp}.log");
        for (var i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(Directory!, $"{prefix}-{stamp}-{i}.log");
        }

        return path;
    }
}
