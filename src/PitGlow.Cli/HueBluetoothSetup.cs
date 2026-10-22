using PitGlow.Backends;
using PitGlow.Config;
using PitGlow.Engine;
using PitGlow.HueBluetooth;

namespace PitGlow.Cli;

/// <summary>
/// "PitGlow hue-bt" (HueBluetooth.bat): pair Hue Bluetooth bulbs with this PC and add them
/// to pitglow.toml as flag lights.
/// </summary>
public static class HueBluetoothSetup
{
    public static readonly TimeSpan ScanTime = TimeSpan.FromSeconds(20);

    public static int Run(string configPath)
    {
        if (!File.Exists(configPath))
        {
            Console.WriteLine($"No {configPath} yet. Run Setup.bat first, then HueBluetooth.bat.");
            return 1;
        }
        var cfg = SettingsFile.Load(configPath);
        var backend = new HueBluetoothBackend();
        var status = backend.Probe(TimeSpan.FromSeconds(5));
        if (!status.Available)
        {
            Console.WriteLine($"Bluetooth isn't usable: {status.Detail}.\nTurn Bluetooth on in Windows Settings > Bluetooth & devices and try again.");
            return 1;
        }

        var paired = HueBluetoothBackend.PairedBulbs(TimeSpan.FromSeconds(10));
        Console.WriteLine(paired.Count == 0
            ? "No Hue bulbs paired with this PC yet."
            : $"Already paired: {string.Join(", ", paired.Select(b => b.Name))}");

        Console.WriteLine("""

            To add bulbs, make them discoverable first, either way:
              - Hue Bluetooth app: Settings > Voice assistants > (Amazon Alexa / Google) > Make discoverable
              - or switch the bulb off for 5 seconds and on again (it can be paired for a while after power-on)
            Close the Hue app on your phone too: a bulb only talks to one device at a time.
            """);
        Console.Write("Press Enter when they're ready (or just Enter to skip pairing)...");
        Console.ReadLine();

        Console.WriteLine($"\nLooking for Hue bulbs for {ScanTime.TotalSeconds:0} seconds...");
        var known = paired.Select(b => b.Address).ToHashSet();
        var found = HueBluetoothBackend.Scan(ScanTime, f =>
            Console.WriteLine($"  found {f.Name} ({(known.Contains(f.Address) ? "already paired" : "new")}, signal {f.Rssi} dBm)"));
        foreach (var bulb in found.Where(f => !known.Contains(f.Address)))
        {
            Console.Write($"  pairing {bulb.Name}... ");
            try
            {
                Console.WriteLine(HueBluetoothBackend.Pair(bulb.Address));
            }
            catch (BackendException ex)
            {
                Console.WriteLine(ex.Message);
            }
        }
        if (found.Count == 0)
            Console.WriteLine("  none heard. If a bulb won't show up, pair it in Windows Settings > Bluetooth & devices > Add device > Bluetooth\n" +
                              "  (it appears as something like \"Hue color lamp\"), then run HueBluetooth.bat again.");

        var bulbs = backend.Discover(TimeSpan.FromSeconds(10));
        int added = 0;
        foreach (var info in bulbs)
        {
            if (cfg.Devices.Any(d => d.Backend == HueBluetoothBackend.BackendId && d.Location == info.Location)) continue;
            cfg.Devices.AddRange(Suggest.Config([info]).Devices);
            added++;
        }
        cfg.HueBluetoothEnabled = true;
        SettingsFile.Save(cfg, configPath);
        Console.WriteLine();
        if (bulbs.Count == 0)
        {
            Console.WriteLine("No paired Hue bulbs yet, so nothing was added.");
            return 1;
        }
        Console.WriteLine(added > 0
            ? $"Added {added} Hue bulb(s) to {configPath}: {string.Join(", ", bulbs.Select(b => b.Label))}. They'll show the race flags."
            : $"Your Hue bulbs were already in {configPath}.");
        Console.WriteLine("Next: Demo.bat to see every flag on them, then PitGlow.bat and race.");
        return 0;
    }
}
