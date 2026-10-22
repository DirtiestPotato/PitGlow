using PitGlow.Config;
using PitGlow.Devices;

namespace PitGlow.Rendering;

/// <summary>A set of LEDs with one role (a fan ring, a pump, a whole GPU).</summary>
public sealed record Group(string Role, IReadOnlyList<int> Leds, int StartLed = 0, bool Reverse = false, string Label = "");

public sealed record Layout(DeviceCfg Cfg, DeviceInfo Info, IReadOnlyList<Group> Groups);

public sealed record LayoutPlan(IReadOnlyList<Layout> Layouts, IReadOnlyList<string> Problems,
                                IReadOnlyList<string> Notes, bool SettingsChanged);

public static class Layouts
{
    /// <summary>
    /// Match saved devices to discovered ones. Order: name + index, then saved hardware
    /// location, then USB vendor/product id. Zones match by name, then by saved index.
    /// When a device or zone is found under a new name, the settings are updated in place
    /// (SettingsChanged = true) so the caller can save them.
    /// </summary>
    public static LayoutPlan Build(Settings cfg, IReadOnlyList<DeviceInfo> infos)
    {
        var layouts = new List<Layout>();
        var problems = new List<string>();
        var notes = new List<string>();
        var taken = new HashSet<DeviceInfo>();
        bool changed = false;

        foreach (var dev in cfg.Devices)
        {
            var pool = infos.Where(i => i.Backend == dev.Backend).ToList();
            var info = pool.FirstOrDefault(i => i.Name == dev.Name && i.Occurrence == dev.Index && !taken.Contains(i));
            if (info is null && !string.IsNullOrEmpty(dev.Location))
                info = pool.FirstOrDefault(i => i.Location == dev.Location && !taken.Contains(i));
            if (info is null && Fingerprint.UsbId(dev.Location) is string usb)
                info = pool.Where(i => i.UsbId == usb && !taken.Contains(i)).ElementAtOrDefault(0);
            if (info is null && string.IsNullOrEmpty(dev.Location))
            {
                // Older settings without a location: accept a single device whose name starts the
                // same way ("Kingston Fury DDR5 RGB" -> "Kingston Fury DDR5 DRAM" in OpenRGB 1.x).
                var stem = NameStem(dev.Name);
                var similar = pool.Where(i => !taken.Contains(i) && NameStem(i.Name) == stem
                                               && !cfg.Devices.Any(d => d.Name == i.Name)).ToList();
                if (stem.Length > 0 && similar.Select(i => i.Name).Distinct().Count() == 1)
                    info = similar.FirstOrDefault(i => i.Occurrence == dev.Index);
            }
            if (info is null)
            {
                problems.Add($"Device not found: \"{dev.Name}\" (index {dev.Index}, {dev.Backend})");
                continue;
            }
            taken.Add(info);

            if (info.Name != dev.Name || info.Occurrence != dev.Index)
            {
                notes.Add($"\"{dev.Name}\" is now called \"{info.Label}\"; settings updated");
                dev.Name = info.Name;
                dev.Index = info.Occurrence;
                changed = true;
            }
            if (!string.IsNullOrEmpty(info.Location) && dev.Location != info.Location)
            {
                dev.Location = info.Location;
                changed = true;
            }

            var groups = new List<Group>();
            if (dev.Role is not null)
            {
                if (dev.Role != Roles.Ignore)
                    groups.Add(new Group(dev.Role, Enumerable.Range(0, info.LedCount).ToArray(), Label: info.Label));
            }
            else
            {
                foreach (var z in dev.Zones)
                {
                    var zone = info.Zone(z.Zone);
                    if (zone is null && z.Index is null && FanNumber(z.Zone) is int num)
                    {
                        // "RGB Port 3" (OpenRGB 0.9) -> "Corsair Fan Header 3" (OpenRGB 1.x)
                        var renamed = info.Zones.Where(c => FanNumber(c.Name) == num).ToList();
                        if (renamed.Count == 1 && dev.Zones.All(o => o.Zone != renamed[0].Name)) z.Index = renamed[0].Index;
                    }
                    if (zone is null && z.Index is int zi && zi >= 0 && zi < info.Zones.Count)
                    {
                        zone = info.Zones[zi];
                        notes.Add($"{info.Label}: zone \"{z.Zone}\" is now \"{zone.Name}\"; settings updated");
                        z.Zone = zone.Name;
                        changed = true;
                    }
                    if (zone is not null && z.Index != zone.Index)
                    {
                        z.Index = zone.Index;
                        changed = true;
                    }
                    if (z.Role == Roles.Ignore) continue;
                    if (zone is null)
                    {
                        problems.Add($"{info.Label}: no zone named \"{z.Zone}\"");
                        continue;
                    }
                    if (zone.Leds.Count == 0)
                    {
                        problems.Add($"{info.Label}: zone \"{zone.Name}\" has 0 LEDs (set its size in OpenRGB)");
                        continue;
                    }
                    groups.Add(new Group(z.Role, zone.Leds, z.StartLed, z.Reverse, $"{info.Label} / {zone.Name}"));
                }
            }
            if (groups.Count > 0) layouts.Add(new Layout(dev, info, groups));
        }
        return new LayoutPlan(layouts, problems, notes, changed);
    }

    /// <summary>First three words, lower case: the part of a device name that survives renames.</summary>
    private static string NameStem(string name)
    {
        var words = name.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 3 ? string.Join(' ', words.Take(3)) : "";
    }

    /// <summary>The number in a fan zone name ("RGB Port 3", "Corsair Fan Header 3"), or null.</summary>
    private static int? FanNumber(string zone)
    {
        var n = zone.ToLowerInvariant();
        if (!(n.Contains("port") || n.Contains("fan"))) return null;
        var digits = new string(n.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return digits.Length > 0 && int.TryParse(digits, out var v) ? v : null;
    }
}
