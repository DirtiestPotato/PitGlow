using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using PitGlow.Config;
using PitGlow.Engine;
using PitGlow.RgbNet;

namespace PitGlow.Cli;

/// <summary>
/// Gets the lighting apps PitGlow relies on running, the right way, before it looks for devices:
/// the OpenRGB service (it runs as administrator, which RAM and GPU lighting need) and iCUE
/// started as a normal program (an iCUE started "as administrator" can't be reached by a normal one).
/// </summary>
internal static class AppStarter
{
    public const string ICueExe = @"C:\Program Files\Corsair\Corsair iCUE5 Software\iCUE.exe";
    public const string OpenRgbExe = @"C:\Program Files\OpenRGB\OpenRGB.exe";
    public static readonly TimeSpan OpenRgbWait = TimeSpan.FromSeconds(25);
    public static readonly TimeSpan ICueWait = TimeSpan.FromSeconds(45);

    public static void Ensure(Settings cfg)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (cfg.OpenRgbEnabled && IsLocal(cfg.Host)) EnsureOpenRgb(cfg.Port);
        if (cfg.ICueEnabled) EnsureICue();
    }

    private static bool IsLocal(string host) => host is "127.0.0.1" or "localhost" or "::1";

    // ---- OpenRGB ----------------------------------------------------------------------------

    private static void EnsureOpenRgb(int port)
    {
        if (Listening(port)) return;
        bool service = ServiceExists("OpenRGB");
        if (!service && !File.Exists(OpenRgbExe)) return;   // not installed here: nothing to start
        Console.WriteLine(service ? "Starting the OpenRGB service..." : "Starting OpenRGB...");
        Log.Info(service ? "OpenRGB not answering; starting its service" : "OpenRGB not answering; starting it");
        // Both need administrator rights: the service to start, OpenRGB itself to reach RAM and GPU lighting.
        var psi = service
            ? new ProcessStartInfo("sc.exe", "start OpenRGB")
            : new ProcessStartInfo(OpenRgbExe, "--server --startminimized");
        if (!Elevation.IsCurrentElevated())
        {
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            Console.WriteLine("  Click Yes on the Windows prompt.");
        }
        psi.WindowStyle = ProcessWindowStyle.Hidden;
        try
        {
            Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Console.WriteLine("  Not started: the prompt was declined. RAM, GPU and motherboard lighting need OpenRGB.");
            return;
        }
        var until = DateTime.UtcNow + OpenRgbWait;
        while (DateTime.UtcNow < until && !Listening(port)) Thread.Sleep(500);
        Console.WriteLine(Listening(port) ? "  OpenRGB is ready." : "  OpenRGB didn't come up; carrying on without it.");
        Thread.Sleep(1500);   // let it finish finding devices before PitGlow asks for them
    }

    private static bool Listening(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", port).Wait(500) && client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            return false;
        }
    }

    private static bool ServiceExists(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", $"query {name}") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    // ---- iCUE -------------------------------------------------------------------------------

    private static void EnsureICue()
    {
        if (!File.Exists(ICueExe) || Process.GetProcessesByName("iCUE").Length > 0) return;
        Console.WriteLine("Starting iCUE...");
        Log.Info("iCUE not running; starting it as a normal program");
        try
        {
            // Always through Explorer: iCUE then runs as a normal program even when PitGlow is
            // administrator, and as Explorer's child it can't attach to PitGlow's console window
            // (started directly, it prints its whole log into PitGlow's window).
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ICueExe}\"") { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            Console.WriteLine($"  Couldn't start iCUE: {ex.Message}");
            return;
        }
        // iCUE's SDK answers a few seconds before iCUE has found the Corsair hardware; asking for
        // devices then gets an empty list. Wait until it lists some, then a moment more so the
        // rest (fans behind a hub come in one by one) are there too.
        var icue = RgbNetBackend.Corsair();
        var until = DateTime.UtcNow + ICueWait;
        int seen = 0;
        while (DateTime.UtcNow < until)
        {
            Thread.Sleep(2000);
            int count;
            try
            {
                count = icue.Discover(TimeSpan.FromSeconds(4)).Count;
            }
            catch (Exception ex) when (ex is Backends.BackendException or TimeoutException)
            {
                continue;   // not answering yet
            }
            if (count > 0 && count == seen)
            {
                Console.WriteLine($"  iCUE is ready ({count} device(s)).");
                return;
            }
            seen = count;   // still finding devices: check again
        }
        Console.WriteLine(seen > 0 ? $"  iCUE found {seen} device(s) so far; carrying on." : "  iCUE didn't list any devices in time; carrying on without Corsair devices.");
    }
}
