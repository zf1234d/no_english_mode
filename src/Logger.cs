using System;
using System.Diagnostics;
using System.IO;

namespace NoEnglishMode;

// Best-effort file logger; all IO failures are swallowed.
internal static class Logger
{
    public const string LogFileName = "log.txt";

    private const int MaxFileBytes = 256 * 1024;

    private static readonly object Gate = new();
    private static string? _path;

    public static void Init(string baseDir)
    {
        try
        {
            Directory.CreateDirectory(baseDir);
            _path = Path.Combine(baseDir, LogFileName);

            if (File.Exists(_path) && new FileInfo(_path).Length > MaxFileBytes)
            {
                File.WriteAllText(_path, string.Empty);
            }

            File.AppendAllText(_path,
                $"---- start {DateTime.Now:yyyy-MM-dd HH:mm:ss} pid={Process.GetCurrentProcess().Id} ----{Environment.NewLine}");
        }
        catch
        {
            _path = null;
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        string? path = _path;
        if (path is null)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}
