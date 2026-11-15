using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PitGlow.Engine;

namespace PitGlow.Cli;

/// <summary>
/// Windows blocks a normal program from talking to one running as administrator. iCUE is often
/// started "as administrator" (right-click, or relaunched by another admin app); PitGlow then
/// can't reach its SDK and the Corsair lights fall back to iCUE's own effects. An admin program
/// can still talk to a normal one, so PitGlow restarts itself as admin when it has to.
/// </summary>
internal static class Elevation
{
    public const string RelaunchedFlag = "--elevated";

    /// <summary>True when a process's token is elevated; null when it can't be read.</summary>
    public static bool? IsElevated(int pid)
    {
        if (!OperatingSystem.IsWindows()) return null;
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token)) return null;
            try
            {
                return GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _) ? elevated != 0 : null;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    public static bool IsCurrentElevated() => IsElevated(Environment.ProcessId) ?? false;

    /// <summary>True when any process with this name runs as administrator.</summary>
    public static bool AnyElevated(string processName) =>
        Process.GetProcessesByName(processName).Any(p => IsElevated(p.Id) == true);

    /// <summary>
    /// If iCUE runs as administrator and PitGlow doesn't, start PitGlow again as administrator
    /// (one UAC prompt) and return true so this copy can exit. False to carry on here.
    /// </summary>
    public static bool RelaunchForICue(string[] argv)
    {
        if (!OperatingSystem.IsWindows() || argv.Contains(RelaunchedFlag)) return false;
        // Only the real PitGlow.exe restarts itself (never a test runner hosting this code).
        if (!string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "PitGlow", StringComparison.OrdinalIgnoreCase)) return false;
        if (IsCurrentElevated() || !AnyElevated("iCUE")) return false;
        Console.WriteLine("iCUE is running as administrator, so PitGlow can't reach it as a normal program.");
        Console.WriteLine("Restarting PitGlow as administrator: click Yes on the Windows prompt.");
        Log.Info("iCUE runs elevated; relaunching PitGlow elevated");
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Environment.CurrentDirectory,
            };
            foreach (var a in argv) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add(RelaunchedFlag);
            Process.Start(psi);
            Console.WriteLine("PitGlow continues in the new window.");
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)   // the prompt was declined
        {
            Console.WriteLine("Not restarted. Corsair devices won't work until iCUE is reopened normally (from the Start menu) or PitGlow runs as administrator.\n");
            Log.Warn("Elevation declined; Corsair devices unavailable while iCUE runs elevated");
            return false;
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
