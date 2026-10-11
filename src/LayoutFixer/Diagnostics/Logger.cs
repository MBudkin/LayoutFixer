using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace LayoutFixer.Diagnostics;

internal static class Logger
{
    private const long MaxBytes = 1_000_000; // ~1 MB hard cap
    private const int MaxQueuedLines = 20_000;

    private static readonly object _gate = new();
    private static readonly string _path = BuildPath();
    private static readonly ConcurrentQueue<string> _queue = new();
    private static readonly AutoResetEvent _signal = new(false);
    private static Thread? _writer;
    private static volatile bool _enabled;

    /// <summary>
    /// Off by default. The tray context toggles this from settings. When off,
    /// nothing is ever written to disk — no keystrokes, no buffer snapshots,
    /// nothing.
    /// </summary>
    /// <remarks>
    /// Lines are queued and written by a background thread: most log calls
    /// come from the low-level keyboard hook, which must never wait for disk
    /// I/O (a slow hook delays every keystroke in the system and Windows may
    /// even remove it).
    /// </remarks>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (value)
            {
                EnsureWriter();
            }
        }
    }

    /// <summary>Full path of the debug log file.</summary>
    public static string FilePath => _path;

    private static string BuildPath()
    {
        try
        {
            return Services.AppPaths.File("debug.log");
        }
        catch
        {
            return "LayoutFixer-debug.log";
        }
    }

    public static void Log(string message)
    {
        if (!_enabled)
        {
            return;
        }

        if (_queue.Count >= MaxQueuedLines)
        {
            // The writer cannot keep up (disk stalled): drop rather than grow.
            return;
        }

        _queue.Enqueue($"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {message}");
        _signal.Set();
    }

    /// <summary>Writes everything queued so far (used on exit).</summary>
    public static void Flush()
    {
        WriteQueued();
    }

    /// <summary>Delete the log file if it exists (used when debug is turned off).</summary>
    public static void Clear()
    {
        try
        {
            lock (_gate)
            {
                while (_queue.TryDequeue(out _))
                {
                }

                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
            }
        }
        catch
        {
            // Best effort.
        }
    }

    private static void EnsureWriter()
    {
        lock (_gate)
        {
            if (_writer is not null)
            {
                return;
            }

            _writer = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "LayoutFixer log writer",
                Priority = ThreadPriority.BelowNormal
            };
            _writer.Start();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
        }
    }

    private static void WriterLoop()
    {
        while (true)
        {
            _signal.WaitOne(500);
            WriteQueued();
        }
    }

    private static void WriteQueued()
    {
        if (_queue.IsEmpty)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                var builder = new StringBuilder();
                while (_queue.TryDequeue(out string? line))
                {
                    builder.Append(line).Append(Environment.NewLine);
                }

                if (builder.Length == 0 || !_enabled)
                {
                    return;
                }

                string? directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                RotateIfNeeded();
                File.AppendAllText(_path, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }

    // Bound growth: once the file exceeds the cap, keep only the most recent
    // tail so the log can never grow unbounded.
    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length < MaxBytes)
            {
                return;
            }

            long keep = 256 * 1024;
            byte[] tail;
            using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (stream.Length <= keep)
                {
                    return;
                }

                stream.Position = stream.Length - keep;
                tail = new byte[keep];
                int read = stream.Read(tail, 0, tail.Length);
                if (read < tail.Length)
                {
                    Array.Resize(ref tail, read);
                }
            }

            File.WriteAllBytes(_path, tail);
        }
        catch
        {
            // Best effort.
        }
    }

    public static string Format(string label, string? text)
    {
        // Callers build log lines eagerly; skip the work when logging is off.
        if (!_enabled)
        {
            return string.Empty;
        }

        if (text is null)
        {
            return $"{label}=<null>";
        }

        var sb = new StringBuilder();
        sb.Append(label).Append("=\"");
        foreach (char c in text)
        {
            sb.Append($"U+{((int)c):X4} ");
        }

        sb.Length = Math.Max(sb.Length - 1, 0);
        sb.Append("\" [").Append(text).Append("]");
        return sb.ToString();
    }
}
