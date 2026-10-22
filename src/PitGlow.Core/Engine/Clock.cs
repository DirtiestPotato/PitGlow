using System.Diagnostics;

namespace PitGlow.Engine;

/// <summary>Monotonic seconds since the process started.</summary>
public static class Clock
{
    private static readonly Stopwatch Watch = Stopwatch.StartNew();
    public static double Now => Watch.Elapsed.TotalSeconds;
}

/// <summary>Tiny append-only log file. Never throws.</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static string? Path { get; set; }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARNING", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}\n{ex}");

    private static void Write(string level, string message)
    {
        if (Path is null) return;
        try
        {
            lock (Gate)
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss,fff} {level} pitglow: {message}{Environment.NewLine}");
        }
        catch
        {
            // logging must never take PitGlow down
        }
    }
}
