using PitGlow.Config;
using PitGlow.Devices;

namespace PitGlow.Engine;

/// <summary>A sensible first config from whatever the backend reports.</summary>
public static class Suggest
{
    private static bool IsFanZone(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("port") || n.Contains("fan");
    }

    public static Settings Config(IEnumerable<DeviceInfo> infos)
    {
        var cfg = new Settings();
        foreach (var info in infos)
        {
            var name = info.Name.ToLowerInvariant();
            if (info.Kind == DeviceKind.Gpu)
            {
                cfg.Devices.Add(Whole(info, 30));
            }
            else if (info.Kind == DeviceKind.Dram)
            {
                var ram = Whole(info, 10);
                ram.ShiftAlert = true;
                cfg.Devices.Add(ram);
            }
            else if (info.Kind == DeviceKind.Cooler || (name.Contains("commander") && info.Kind != DeviceKind.Fan))
            {
                var zones = new List<ZoneCfg>();
                foreach (var zone in info.Zones)
                {
                    string role;
                    if (zone.Leds.Count == 0) role = Roles.Ignore;
                    else if (zone.Name.Contains("pump", StringComparison.OrdinalIgnoreCase)) role = Roles.Flags;
                    else if (IsFanZone(zone.Name)) role = Roles.Tach;
                    else role = Roles.Ignore;
                    // Empty fan headers still get a tach role, so they light up once sized in OpenRGB.
                    if (zone.Leds.Count == 0 && IsFanZone(zone.Name)) role = Roles.Tach;
                    zones.Add(new ZoneCfg { Zone = zone.Name, Role = role, Index = zone.Index });
                }
                double fps = name.Contains("commander") ? 20 : 30;
                if (zones.Any(z => z.Role == Roles.Tach))
                    cfg.Devices.Add(new DeviceCfg { Name = info.Name, Backend = info.Backend, Index = info.Occurrence, MaxFps = fps, Location = Loc(info), Zones = zones });
                else   // a cooler with no fan ports (a pump): a flag light that also flashes at the shift point
                {
                    var pump = Whole(info, fps);
                    pump.ShiftAlert = true;
                    cfg.Devices.Add(pump);
                }
            }
            else if (info.Kind == DeviceKind.Light && info.Backend == "hue")
            {
                cfg.Devices.Add(Whole(info, 25));   // Hue Entertainment area: flags on every light
            }
            else if (info.Kind == DeviceKind.Light && info.Backend == "huebt")
            {
                cfg.Devices.Add(Whole(info, 10));   // Hue Bluetooth bulb: ~10 changes a second
            }
            else if (info.Kind == DeviceKind.Mouse)
            {
                cfg.Devices.Add(Whole(info, 20));
            }
            else if (info.Kind == DeviceKind.Fan || (info.Kind == DeviceKind.LedStrip && info.Backend != "openrgb"))
            {
                // Vendor SDKs report each fan (or strip) as its own device with one zone.
                cfg.Devices.Add(new DeviceCfg
                {
                    Name = info.Name, Backend = info.Backend, Index = info.Occurrence, MaxFps = 30, Location = Loc(info),
                    Zones = [.. info.Zones.Select(z => new ZoneCfg { Zone = z.Name, Role = Roles.Tach, Index = z.Index })],
                });
            }
        }
        return cfg;
    }

    private static string? Loc(DeviceInfo info) => string.IsNullOrEmpty(info.Location) ? null : info.Location;

    private static DeviceCfg Whole(DeviceInfo info, double fps) =>
        new() { Name = info.Name, Backend = info.Backend, Index = info.Occurrence, Role = Roles.Flags, MaxFps = fps, Location = Loc(info) };
}
