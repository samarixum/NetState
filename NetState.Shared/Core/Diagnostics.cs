using System;
using System.IO;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace NetState.Shared.Core;

/* :: :: Diagnostics :: START :: */

/// <summary>
/// Diagnostic logging utility for NetState.
/// Handles session-based logging for both Client and Server.
/// </summary>
public static class Diagnostics {

    private const bool IsTraceEnabled =
#if DEBUG
        true;
#else
        false;
#endif

    // Trace writer only exists in Debug builds
    private static StreamWriter? _traceWriter; // Master log (Everything)

    // These writers exist in all builds so logging always works
    private static StreamWriter? _debugWriter; // Just Diagnostics.Log / Info
    private static StreamWriter? _bugWriter;   // Just Diagnostics.Bug
    private static readonly object _lock = new();

    /* :: :: Initialization :: START :: */

    /// <summary>
    /// Initializes logging for the specified application component.
    /// </summary>
    /// <param name="appName">The name of the application component (e.g., "Server" or "Client").</param>
    public static void Initialize(string appName) {
        string logDirectory = string.Empty;
        try {
            // Use local app data or current directory for logs
            string baseDir = AppContext.BaseDirectory;
            string logDir = Path.Combine(baseDir, "logs", appName);

            // 1. Cleanup old logs (keep last 24 hours)
            CleanLogDirectory(logDir, retentionHours: 24);

            // 2. Create the new log subdirectory for this session
            logDirectory = CreateSessionLogDirectory(logDir);

            // 3. Define Paths
            string tracePath = Path.Combine(logDirectory, "trace.log");
            string debugPath = Path.Combine(logDirectory, "debug.log");
            string bugPath = Path.Combine(logDirectory, "exception.log");

            // 4. Open Streams (Shared access allowed)
            // Trace Writer (Master) - Debug builds only
            if (IsTraceEnabled) {
                var fsTrace = new FileStream(tracePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _traceWriter = new StreamWriter(fsTrace) { AutoFlush = true };
            }

            // Debug Writer (all builds)
            var fsDebug = new FileStream(debugPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _debugWriter = new StreamWriter(fsDebug) { AutoFlush = true };

            // Bug Writer (all builds)
            var fsBug = new FileStream(bugPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _bugWriter = new StreamWriter(fsBug) { AutoFlush = true };

            // 5. Hook System.Diagnostics.Trace to the Master Trace Log (Debug only)
            if (IsTraceEnabled && _traceWriter != null) {
                System.Diagnostics.Trace.Listeners.Add(new TextWriterTraceListener(_traceWriter));
                System.Diagnostics.Trace.AutoFlush = true;
            }

            Log($"[System] Logging initialized for {appName} at {DateTime.Now}");

        } catch (Exception ex) {
            Console.Error.WriteLine($"CRITICAL: Failed to init loggers. {ex.Message}");
        }
    }

    /* :: :: Initialization :: END :: */
    // //
    /* :: :: Internal Helpers :: START :: */

    private static void CleanLogDirectory(string logDir, int retentionHours) {
        if (!Directory.Exists(logDir)) {
            Directory.CreateDirectory(logDir);
            return;
        }

        try {
            DateTime threshold = DateTime.Now.AddHours(-retentionHours);
            DirectoryInfo directoryInfo = new DirectoryInfo(logDir);

            foreach (DirectoryInfo subDir in directoryInfo.GetDirectories()) {
                if (subDir.LastWriteTime < threshold) {
                    try {
                        subDir.Delete(true);
                    } catch {
                        // skip
                    }
                }
            }
        } catch {
            // continue
        }
    }

    private static string CreateSessionLogDirectory(string logDir) {
        string logSubdir = DateTime.Now.ToString("dd-MM-HH-mm");
        string logDirectory = Path.Combine(logDir, logSubdir);

        if (!Directory.Exists(logDirectory)) {
            Directory.CreateDirectory(logDirectory);
            return logDirectory;
        }

        string collisionSuffix = DateTime.Now.ToString("ss");
        string collisionDirectory = Path.Combine(logDir, $"{logSubdir}_{collisionSuffix}");
        if (!Directory.Exists(collisionDirectory)) {
            Directory.CreateDirectory(collisionDirectory);
            return collisionDirectory;
        }

        string fallbackDirectory = Path.Combine(logDir, $"{logSubdir}_{collisionSuffix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fallbackDirectory);
        return fallbackDirectory;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteTraceInternal(string message, string? stack = null) {
        if (!IsTraceEnabled || _traceWriter == null) {
            return;
        }

        _traceWriter.WriteLine(message);
        if (stack != null) {
            _traceWriter.WriteLine(stack);
        }
    }

    /* :: :: Internal Helpers :: END :: */
    // //
    /* :: :: Logging Methods :: START :: */

    /// <summary>
    /// Log a trace message to trace.log, only in Debug builds.
    /// </summary>
    public static void Trace(string message) {
        if (!IsTraceEnabled || _traceWriter == null) {
            return;
        }

        lock (_lock) {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string formattedMsg = $"[{timestamp}] [TRACE] {message}";
            _traceWriter.WriteLine(formattedMsg);
        }
    }

    /// <summary>
    /// Logs an informational message to debug.log and trace.log.
    /// </summary>
    public static void Info(string message) {
        if (_debugWriter == null) return;

        lock (_lock) {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string formattedMsg = $"[{timestamp}] [INFO] {message}";
            _debugWriter.WriteLine(formattedMsg);
            WriteTraceInternal(formattedMsg);
        }
    }

    /// <summary>
    /// Logs a general log message to debug.log and trace.log.
    /// </summary>
    public static void Log(string message) {
        if (_debugWriter == null) return;

        lock (_lock) {
            string timestamp = DateTime.Now.ToString("HH:ss");
            string formattedMsg = $"[{timestamp}] [Log] {message}";
            _debugWriter.WriteLine(formattedMsg);
            WriteTraceInternal(formattedMsg);
        }
    }

    /// <summary>
    /// Logs a bug/exception message to exception.log and trace.log.
    /// </summary>
    public static void Bug(string message, Exception? ex = null) {
        if (_bugWriter == null) return;

        lock (_lock) {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string header = $"[{timestamp}] [BUG] {message}";
            string? stack = ex != null ? $"[{timestamp}] [STACK] {ex}" : null;

            _bugWriter.WriteLine(header);
            if (stack != null) _bugWriter.WriteLine(stack);
            WriteTraceInternal(header, stack);
        }
    }

    /// <summary>
    /// Closes all active log writers.
    /// </summary>
    public static void Close() {
        lock (_lock) {
            _debugWriter?.Close();
            _bugWriter?.Close();

            if (IsTraceEnabled) {
                _traceWriter?.Close();
                System.Diagnostics.Trace.Listeners.Clear();
            }
        }
    }

    /* :: :: Logging Methods :: END :: */
}

/* :: :: Diagnostics :: END :: */
