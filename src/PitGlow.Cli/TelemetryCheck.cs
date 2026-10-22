using System.Diagnostics;
using System.Globalization;
using System.Text;
using PitGlow.Config;
using PitGlow.Sources;

namespace PitGlow.Cli;

/// <summary>
/// "PitGlow telemetry": shows what each game's telemetry looks like right now, without
/// touching any lights. Drive while it runs; the report is also saved to telemetry-check.txt.
/// </summary>
public static class TelemetryCheck
{
    private static readonly string[] Blocks =
    [
        @"Local\acpmf_physics", @"Local\acpmf_graphics", @"Local\acpmf_static",
        @"Local\acevo_pmf_physics", @"Local\acevo_pmf_graphics", @"Local\acevo_pmf_static",
        @"Local\IRSDKMemMapFileName",
    ];

    public static int Run(string reportPath, double seconds)
    {
        var report = new StringBuilder();
        void Say(string line)
        {
            Console.WriteLine(line);
            report.AppendLine(line);
        }

        Say($"PitGlow {typeof(Program).Assembly.GetName().Version?.ToString(3)} telemetry check, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Say($"Running as administrator: {IsAdmin()}");
        var games = Process.GetProcesses().Select(p => p.ProcessName)
            .Where(n => n.Contains("acr", StringComparison.OrdinalIgnoreCase) || n.Contains("assetto", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("iracing", StringComparison.OrdinalIgnoreCase) || n.StartsWith("AC", StringComparison.Ordinal))
            .Distinct().OrderBy(n => n).ToList();
        Say($"Game processes: {(games.Count > 0 ? string.Join(", ", games) : "none found")}");
        Say($"\nGet in the car and drive. Watching for {seconds:0} seconds...\n");

        var source = (AutoSource)SourceFactory.Make(new GameCfg());
        var clock = Stopwatch.StartNew();
        double nextLine = 0;
        string? lastGame = null;
        int samples = 0;
        double maxRpm = 0;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            double now = clock.Elapsed.TotalSeconds;
            var s = source.Read(now);
            if (s is not null)
            {
                samples++;
                maxRpm = Math.Max(maxRpm, s.Rpm);
                if (source.Game != lastGame) Say($"[{now,5:0.0}s] PitGlow is following: {source.Game}");
                lastGame = source.Game;
            }
            if (now >= nextLine)
            {
                nextLine = now + 2;
                Say($"[{now,5:0.0}s] " + (s is null
                    ? "PitGlow sees no game driving"
                    : string.Create(CultureInfo.InvariantCulture,
                        $"rpm {s.Rpm:0}  max {s.MaxRpm?.ToString("0") ?? "-"}  shift {s.ShiftRpm?.ToString("0") ?? "-"}  pit limiter {s.PitLimiter}  flags [{string.Join(' ', s.Flags.Select(f => f.ConfigName()))}]")));
                foreach (var name in Blocks) Say("         " + Describe(name));
            }
            Thread.Sleep(50);
        }
        source.Close();
        Say($"\nDone: {samples} readings from a game, highest rpm {maxRpm:0}.");
        Say(samples > 0 ? "Telemetry works; PitGlow.bat should light up while you drive."
                        : "PitGlow never saw a game driving. Send this report (telemetry-check.txt) to get it fixed.");
        try
        {
            File.WriteAllText(reportPath, report.ToString());
            Console.WriteLine($"\nSaved to {reportPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nCould not save the report: {ex.Message}");
        }
        return samples > 0 ? 0 : 1;
    }

    private static string Describe(string name)
    {
        var block = new SharedBlock(name);
        if (!block.Open()) return $"{name,-28} not there";
        try
        {
            string Int(int off) => block.Capacity >= off + 4 ? block.Read<int>(off).ToString(CultureInfo.InvariantCulture) : "?";
            string Float(int off) => block.Capacity >= off + 4 ? block.Read<float>(off).ToString("0.##", CultureInfo.InvariantCulture) : "?";
            string detail = name switch
            {
                _ when name.EndsWith("physics") => $"packet {Int(0)}  gear {Int(16)}  rpm {Int(20)}  speed {Float(28)}  pit {Int(248)}  curMax int {Int(588)} / float {Float(588)}",
                _ when name.EndsWith("graphics") => $"packet {Int(0)}  status {Int(4)}  session {Int(8)}",
                _ when name.EndsWith("static") => $"sm '{Text(block, 0, 15)}' game '{Text(block, 30, 15)}'  car '{Text(block, 68, 33)}'  maxRpm {Int(412)}",
                _ => $"status {Int(4)}",
            };
            return $"{name,-28} {block.Capacity,6} bytes  {detail}";
        }
        finally
        {
            block.Close();
        }
    }

    private static string Text(SharedBlock block, int offset, int chars)
    {
        if (block.Capacity < offset + chars * 2) return "?";
        var bytes = new byte[chars * 2];
        block.ReadBytes(offset, bytes, bytes.Length);
        var text = Encoding.Unicode.GetString(bytes);
        int end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    }

    private static bool IsAdmin()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
