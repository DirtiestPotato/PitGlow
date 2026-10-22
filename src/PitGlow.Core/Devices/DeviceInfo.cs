using System.Text.RegularExpressions;

namespace PitGlow.Devices;

/// <summary>Kinds of lighting device, in OpenRGB's device-type order.</summary>
public enum DeviceKind
{
    Motherboard, Dram, Gpu, Cooler, LedStrip, Keyboard, Mouse, Mousemat, Headset, HeadsetStand,
    Gamepad, Light, Speaker, Virtual, Storage, Case, Microphone, Accessory, Keypad, Unknown,
}

/// <summary>A named group of LEDs inside a device (a fan, a RAM stick, a key row).</summary>
public sealed record LedZone(string Name, int Index, IReadOnlyList<int> Leds);

/// <summary>
/// One lighting device as any backend reports it. The fingerprint (location / USB id)
/// lets saved settings survive renames between backend versions.
/// </summary>
public sealed class DeviceInfo
{
    public required string Backend { get; init; }
    public required string Name { get; init; }
    public int Occurrence { get; init; }         // n-th device with this name
    public int Position { get; init; }           // index in the backend's list at discovery
    public DeviceKind Kind { get; init; } = DeviceKind.Unknown;
    public string Location { get; init; } = "";
    public string Serial { get; init; } = "";
    public int LedCount { get; init; }
    public required IReadOnlyList<LedZone> Zones { get; init; }
    public required IReadOnlyList<Rgb> OriginalColors { get; init; }
    public string? OriginalMode { get; init; }
    public IReadOnlyList<string> ModeNames { get; init; } = [];

    /// <summary>Backend-private snapshot taken at discovery (used to restore lighting).</summary>
    public object? BackendState { get; init; }

    public string Label => Occurrence == 0 ? Name : $"{Name} #{Occurrence + 1}";
    public string KindName => Kind.ToString().ToUpperInvariant();
    public string? UsbId => Fingerprint.UsbId(Location);

    public LedZone? Zone(string name) => Zones.FirstOrDefault(z => z.Name == name);
}

public static partial class Fingerprint
{
    [GeneratedRegex(@"vid_([0-9a-f]{4}).*?pid_([0-9a-f]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex UsbPattern();

    /// <summary>"1B1C:0C32" from a HID location string, or null.</summary>
    public static string? UsbId(string? location)
    {
        if (string.IsNullOrEmpty(location)) return null;
        var m = UsbPattern().Match(location);
        return m.Success ? $"{m.Groups[1].Value.ToUpperInvariant()}:{m.Groups[2].Value.ToUpperInvariant()}" : null;
    }
}
