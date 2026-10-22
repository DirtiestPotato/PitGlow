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
    public int Index { get; set; }              // which one, when several devices share a name
    public string? Role { get; set; }           // whole-device role (GPU, RAM); else per zone
    public double MaxFps { get; set; } = 30.0;
    /// <summary>Hardware location reported by the backend; finds the device again after a rename.</summary>
    public string? Location { get; set; }
    public List<ZoneCfg> Zones { get; set; } = [];

    public bool SameAs(DeviceCfg o) =>
        Name == o.Name && Index == o.Index && Role == o.Role && MaxFps == o.MaxFps &&
        (Location ?? "") == (o.Location ?? "") && Zones.SequenceEqual(o.Zones);
}

public sealed class GameCfg
{
    public static readonly string[] Sources = ["auto", "iracing", "acevo", "acrally", "wreckfest2", "beamng", "demo"];
    public string Source { get; set; } = "auto";
    public int Wreckfest2Port { get; set; } = 23123;
    public int BeamngPort { get; set; } = 4444;
}

public sealed class Settings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 6742;
    public GameCfg Game { get; set; } = new();
    public TachStyle Tach { get; set; } = new();
    public double FlagBrightness { get; set; } = 1.0;
    public string WhenClear { get; set; } = "original";   // original | off | #RRGGBB
    public double LingerSeconds { get; set; } = 1.0;
    public List<Flag> Priority { get; set; } = [.. Effects.DefaultPriority];
    public Dictionary<Flag, FlagStyle> FlagStyles { get; set; } = new(Effects.DefaultFlagStyles);
    public List<DeviceCfg> Devices { get; set; } = [];
}
