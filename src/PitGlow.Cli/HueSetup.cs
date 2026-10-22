using PitGlow.Backends;
using PitGlow.Config;
using PitGlow.Devices;
using PitGlow.Engine;
using PitGlow.Hue;

namespace PitGlow.Cli;

/// <summary>
/// "PitGlow hue" (HueSetup.bat): find the Hue Bridge, pair with it (press its button),
/// and add every Entertainment area to pitglow.toml as a flag device.
/// </summary>
public static class HueSetup
{
    public static readonly TimeSpan PairWindow = TimeSpan.FromSeconds(60);

    public static int Run(string configPath, string? host)
    {
        if (!File.Exists(configPath))
        {
            Console.WriteLine($"No {configPath} yet. Run Setup.bat first, then HueSetup.bat.");
            return 1;
        }
        var cfg = SettingsFile.Load(configPath);

        // 1. Find the bridge.
        string? ip = host ?? (cfg.Hue.Bridge.Length > 0 ? cfg.Hue.Bridge : null);
        if (ip is null)
        {
            Console.WriteLine("Looking for your Hue Bridge...");
            IReadOnlyList<string> found;
            try
            {
                found = HueBridge.FindBridges(TimeSpan.FromSeconds(8));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Couldn't ask Philips' finder service ({ex.Message}).");
                found = [];
            }
            if (found.Count == 0)
            {
                Console.WriteLine("No Hue Bridge found. In the Hue app open Settings > My Hue system > (your bridge) to see its IP address,\n" +
                                  "then run:   PitGlow.exe hue --host 192.168.x.x");
                return 1;
            }
            if (found.Count > 1) Console.WriteLine($"Found {found.Count} bridges ({string.Join(", ", found)}); using the first. Use --host to pick another.");
            ip = found[0];
        }
        HueBridgeInfo bridge;
        using (var probe = new HueBridge(ip))
        {
            try
            {
                bridge = probe.Describe();
            }
            catch (BackendException ex)
            {
                Console.WriteLine($"No Hue Bridge answering at {ip}: {ex.Message}");
                return 1;
            }
        }
        Console.WriteLine($"Hue Bridge \"{bridge.Name}\" at {ip}");

        // 2. Pair, unless the saved key still works.
        bool keyWorks = false;
        if (cfg.Hue.Paired && cfg.Hue.Bridge == ip)
        {
            using var b = new HueBridge(ip, cfg.Hue.AppKey);
            try { b.ApplicationId(); keyWorks = true; } catch (BackendException) { /* pair again */ }
        }
        if (!keyWorks)
        {
            var pairing = Pair(ip);
            if (pairing is null)
            {
                Console.WriteLine("\nThe button wasn't pressed in time. Run HueSetup.bat again.");
                return 1;
            }
            cfg.Hue.AppKey = pairing.AppKey;
            cfg.Hue.ClientKey = pairing.ClientKey;
            Console.WriteLine("Paired.");
        }
        cfg.Hue.Bridge = ip;
        cfg.Hue.Enabled = true;

        // 3. Entertainment areas become flag devices.
        IReadOnlyList<DeviceInfo> areas;
        try
        {
            areas = new HueBackend(cfg.Hue).Discover(TimeSpan.FromSeconds(8));
        }
        catch (BackendException ex)
        {
            SettingsFile.Save(cfg, configPath);   // keep the pairing
            Console.WriteLine($"\nPaired, but {ex.Message}.\nThen run HueSetup.bat again.");
            return 1;
        }
        int added = 0;
        Console.WriteLine("\nEntertainment areas:");
        foreach (var area in areas)
        {
            var state = (HueArea)area.BackendState!;
            Console.WriteLine($"  {area.Label}: {string.Join(", ", area.Zones.Select(z => z.Name))}");
            if (state.Status == "active")
                Console.WriteLine($"    ! another app is streaming to it right now (iCUE's Hue integration or Hue Sync). " +
                                  "Turn that off while racing, or PitGlow and it will fight over the lights.");
            bool known = cfg.Devices.Any(d => d.Backend == HueBackend.BackendId && d.Location == area.Location);
            if (!known)
            {
                cfg.Devices.AddRange(Suggest.Config([area]).Devices);
                added++;
            }
        }
        SettingsFile.Save(cfg, configPath);
        Console.WriteLine(added > 0
            ? $"\nAdded {added} Hue area(s) to {configPath}; their lights will show the race flags."
            : $"\nYour Hue areas were already in {configPath}.");
        Console.WriteLine("Next: Demo.bat to see every flag on your Hue lights, then PitGlow.bat and race.");
        return 0;
    }

    private static HuePairing? Pair(string ip)
    {
        Console.WriteLine("\n>>> Press the round link button on top of your Hue Bridge now. <<<");
        using var b = new HueBridge(ip);
        var until = DateTime.UtcNow + PairWindow;
        while (DateTime.UtcNow < until)
        {
            var pairing = b.TryPair(Environment.MachineName);
            if (pairing is not null) return pairing;
            int left = (int)(until - DateTime.UtcNow).TotalSeconds;
            Console.Write($"\r    waiting for the button... {left,2}s ");
            Thread.Sleep(1000);
        }
        return null;
    }
}
