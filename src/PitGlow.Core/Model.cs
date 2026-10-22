namespace PitGlow;

/// <summary>Every race flag PitGlow knows, independent of the game.</summary>
public enum Flag
{
    Green,
    Yellow,             // local / sector yellow
    FullCourseYellow,   // caution, safety car, FCY
    Blue,
    White,              // slow car or last lap, depending on the sim
    Checkered,
    Red,
    Black,
    BlackWhite,         // warning / furled black
    Meatball,           // repair / mechanical
    Disqualified,
    Debris,             // slippery surface (red/yellow stripes)
}

public static class Flags
{
    /// <summary>Config-file name of a flag: FullCourseYellow -> "FULL_COURSE_YELLOW".</summary>
    public static string ConfigName(this Flag flag) => flag switch
    {
        Flag.FullCourseYellow => "FULL_COURSE_YELLOW",
        Flag.BlackWhite => "BLACK_WHITE",
        _ => flag.ToString().ToUpperInvariant(),
    };

    public static Flag Parse(string name)
    {
        foreach (var flag in Enum.GetValues<Flag>())
            if (flag.ConfigName() == name.Trim().ToUpperInvariant())
                return flag;
        throw new ConfigException($"Unknown flag {name}; use one of {string.Join(", ", Enum.GetValues<Flag>().Select(f => f.ConfigName()))}");
    }

    public static readonly IReadOnlySet<Flag> None = new HashSet<Flag>();
}

/// <summary>
/// One telemetry reading while the player is driving. Sources return null instead
/// of a Sample when the player is not in the car (menus, garage, replays, game closed).
/// </summary>
public sealed record Sample(string Game)
{
    public double Rpm { get; init; }
    public double? FirstRpm { get; init; }   // gauge starts filling here
    public double? ShiftRpm { get; init; }   // gauge is full here
    public double? BlinkRpm { get; init; }   // gauge flashes from here
    public double? MaxRpm { get; init; }     // limiter / redline, used for fallbacks
    public bool ShiftNow { get; init; }      // the game itself says "shift"
    public bool PitLimiter { get; init; }
    public IReadOnlySet<Flag> Flags { get; init; } = PitGlow.Flags.None;
}

/// <summary>A user-facing problem with the settings file.</summary>
public sealed class ConfigException(string message, Exception? inner = null) : Exception(message, inner);
