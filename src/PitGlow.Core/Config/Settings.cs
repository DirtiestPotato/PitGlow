namespace PitGlow.Config;

public static class Roles
{
    public const string Tach = "tach";
    public const string Flags = "flags";
    public const string Ignore = "ignore";
    public static readonly string[] All = [Tach, Flags, Ignore];

    public static string Check(string role, string where) =>
        All.Contains(role) ? role : throw new ConfigException($"{where}: role must be tach, flags or ignore, got \"{role}\"");
}

public sealed record ZoneCfg
{
    public required string Zone { get; set; }
    public string Role { get; set; } = Roles.Ignore;
    public int StartLed { get; set; }
    public bool Reverse { get; set; }
    /// <summary>Position of the zone inside its device; finds the zone again after a rename.</summary>
    public int? Index { get; set; }
}

public sealed class DeviceCfg
{
    public required string Name { get; set; }
    /// <summary>Which lighting route drives this device: openrgb (default), icue, razer.</summary>
    public string Backend { get; set; } = "openrgb";
    public int Index { get; set; }              // which one, when several devices share a name
    public string? Role { get; set; }           // whole-device role (GPU, RAM); else per zone
    public double MaxFps { get; set; } = 30.0;
    /// <summary>Flag devices only: flash the shift alert while the car is at its shift point.</summary>
    public bool ShiftAlert { get; set; }
    /// <summary>Hardware location reported by the backend; finds the device again after a rename.</summary>
    public string? Location { get; set; }
    public List<ZoneCfg> Zones { get; set; } = [];

    public bool SameAs(DeviceCfg o) =>
        Name == o.Name && Backend == o.Backend && Index == o.Index && Role == o.Role && MaxFps == o.MaxFps &&
        ShiftAlert == o.ShiftAlert &&
        (Location ?? "") == (o.Location ?? "") && Zones.SequenceEqual(o.Zones);
}

/// <summary>Philips Hue Bridge pairing, written by "PitGlow hue" (HueSetup.bat).</summary>
public sealed class HueCfg
{
    public bool Enabled { get; set; } = true;
    public string Bridge { get; set; } = "";      // bridge IP address
    public string AppKey { get; set; } = "";      // hue-application-key from pairing
    public string ClientKey { get; set; } = "";   // streaming key from pairing (hex)
    public bool Paired => Bridge.Length > 0 && AppKey.Length > 0 && ClientKey.Length > 0;
}

public sealed class GameCfg
{
    public static readonly string[] Sources = ["auto", "iracing", "acevo", "acrally", "wreckfest2", "beamng", "demo"];
    public string Source { get; set; } = "auto";
    public int Wreckfest2Port { get; set; } = 23124;   // 23123 is left to motion rigs / SimHub
    /// <summary>
    /// Let PitGlow add its entry to Wreckfest 2's telemetry list. Off by default: motion software
    /// backs that file up and restores it, so PitGlow's edits end up in its backup. With a motion
    /// rig, forward from the motion software to PitGlow's port instead.
    /// </summary>
    public bool Wreckfest2ManageConfig { get; set; }
    public int BeamngPort { get; set; } = 4444;
}

public sealed class Settings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 6742;
    public bool OpenRgbEnabled { get; set; } = true;
    public bool ICueEnabled { get; set; } = true;
    /// <summary>
    /// Cap on updates per second for each iCUE device. A Commander Core's fans and pump all share
    /// one small hub, which starts dropping writes when iCUE is pushed too hard.
    /// </summary>
    public double ICueMaxFps { get; set; } = 15.0;
    /// <summary>
    /// Keep iCUE devices from the first race until PitGlow closes, instead of handing them back to
    /// iCUE whenever the game pauses or loses focus (the back-and-forth can stall iCUE).
    /// </summary>
    public bool ICueHoldControl { get; set; } = true;
    /// <summary>
    /// Drive Corsair Commander Core hubs (Elite Capellix coolers) directly, without iCUE. Only
    /// colors are sent; the hub keeps running the fan and pump curves saved in its memory.
    /// </summary>
    public bool CorsairHubEnabled { get; set; } = true;
    public bool RazerEnabled { get; set; } = true;
    public HueCfg Hue { get; set; } = new();
    /// <summary>Hue lights (Bridge and Bluetooth) show flags only and stay off when no flag is out.</summary>
    public bool HueFlagsOnly { get; set; } = true;
    public bool HueBluetoothEnabled { get; set; } = true;
    public GameCfg Game { get; set; } = new();
    /// <summary>
    /// "traditional": each zone follows its role (tach or flags).
    /// "all": every zone shows the RPM gauge, and every zone shows the flag while one is out.
    /// </summary>
    public string Mode { get; set; } = "traditional";
    public TachStyle Tach { get; set; } = new();
    public double FlagBrightness { get; set; } = 1.0;
    public string WhenClear { get; set; } = "original";   // original | off | #RRGGBB
    /// <summary>What flag devices show in games without race flags: "rpm" (rev color) or "clear".</summary>
    public string WhenNoFlags { get; set; } = "rpm";
    public double LingerSeconds { get; set; } = 1.0;
    /// <summary>How devices with shift_alert = true flash at the shift point.</summary>
    public FlagStyle ShiftAlertStyle { get; set; } = new(FlagPattern.Flash, [new(255, 255, 255)], 8.0);
    public List<Flag> Priority { get; set; } = [.. Effects.DefaultPriority];
    public Dictionary<Flag, FlagStyle> FlagStyles { get; set; } = new(Effects.DefaultFlagStyles);
    public List<DeviceCfg> Devices { get; set; } = [];
}
