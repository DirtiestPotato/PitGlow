namespace PitGlow.Sources;

/// <summary>
/// Kunos shared memory: Assetto Corsa EVO, Assetto Corsa Rally (and AC1 / ACC).
/// AC EVO uses Local\acevo_pmf_* with its own graphics layout (flags included).
/// AC Rally, ACC and the original AC share Local\acpmf_* and the same leading
/// physics/static layout, which is all the RPM gauge needs. Rally has no race flags.
/// </summary>
public abstract class KunosSource(Func<string, IMemoryBlock>? opener) : ITelemetrySource
{
    public const int AcLive = 2;
    public const double StaleSeconds = 1.5;

    private readonly Func<string, IMemoryBlock> _opener = opener ?? (name => new SharedBlock(name));
    private long? _lastPacket;
    private double _lastChange;

    protected abstract string Prefix { get; }
    public abstract string Game { get; }

    internal IMemoryBlock? Physics { get; private set; }
    internal IMemoryBlock? Graphics { get; private set; }
    internal IMemoryBlock? Static { get; private set; }
    public bool IsOpen => Physics is not null;

    private bool OpenBlocks()
    {
        if (Physics is not null) return true;
        var physics = _opener(Prefix + "physics");
        if (!physics.Open()) return false;
        var graphics = _opener(Prefix + "graphics");
        var stat = _opener(Prefix + "static");
        Physics = physics;
        Graphics = graphics.Open() ? graphics : null;
        Static = stat.Open() ? stat : null;
        _lastPacket = null;
        return true;
    }

    public void Close()
    {
        Physics?.Close();
        Graphics?.Close();
        Static?.Close();
        Physics = Graphics = Static = null;
    }

    /// <summary>
    /// The physics page must keep changing; a closed or paused game leaves stale data.
    /// packetId is the usual heartbeat, but AC Rally doesn't always advance it, so any
    /// change in the first 64 bytes (packet, pedals, gear, rpm, speed, velocity, g-forces)
    /// counts as a beat too.
    /// </summary>
    private bool Fresh(double now)
    {
        long beat = Heartbeat(Physics!);
        if (_lastPacket is null)   // need to see it move at least once
        {
            _lastPacket = beat;
            _lastChange = now;
            return false;
        }
        if (beat != _lastPacket)
        {
            _lastPacket = beat;
            _lastChange = now;
            return true;
        }
        if (now - _lastChange > StaleSeconds)
        {
            Close();   // release our handle so the block can go away
            return false;
        }
        return true;
    }

    internal static long Heartbeat(IMemoryBlock physics)
    {
        long h = 17;
        for (int off = 0; off < 64; off += 8) h = h * 31 + physics.Read<long>(off);
        return h;
    }

    public Sample? Read(double now)
    {
        if (!OpenBlocks() || !Fresh(now)) return null;
        return Build();
    }

    protected abstract Sample? Build();
}

public sealed class ACEvoSource(Func<string, IMemoryBlock>? opener = null) : KunosSource(opener)
{
    private static readonly Dictionary<int, Flag> EvoFlags = new()
    {
        [1] = Flag.White, [2] = Flag.Green, [3] = Flag.Red, [4] = Flag.Blue, [5] = Flag.Yellow,
        [6] = Flag.Black, [7] = Flag.BlackWhite, [8] = Flag.Checkered, [9] = Flag.Meatball, [10] = Flag.Debris,
    };

    protected override string Prefix => @"Local\acevo_pmf_";
    public override string Game => "Assetto Corsa EVO";

    public static IReadOnlySet<Flag> EvoFlagSet(int driverFlag, int globalFlag)
    {
        var flags = new HashSet<Flag>();
        if (EvoFlags.TryGetValue(driverFlag, out var d)) flags.Add(d);
        if (EvoFlags.TryGetValue(globalFlag, out var g))
            // A yellow shown to the whole field is a full-course yellow.
            flags.Add(g == Flag.Yellow ? Flag.FullCourseYellow : g);
        return flags;
    }

    protected override Sample? Build()
    {
        var p = Physics!;
        var g = Graphics;
        if (g is null || g.Read<int>(4) != AcLive) return null;
        int top = p.Read<int>(588);                              // currentMaxRpm
        return new Sample(Game)
        {
            Rpm = p.Read<int>(20),
            MaxRpm = top > 0 ? top : null,
            ShiftNow = g.Read<byte>(43) != 0,                     // is_change_up_rpm
            PitLimiter = p.Read<int>(248) != 0,
            Flags = EvoFlagSet(g.Read<int>(2404), g.Read<int>(2408)),
        };
    }
}

/// <summary>AC Rally, ACC and AC1 (Local\acpmf_*): RPM, max RPM and pit limiter.</summary>
public sealed class ACClassicSource(Func<string, IMemoryBlock>? opener = null) : KunosSource(opener)
{
    public const int AcReplay = 1, AcPause = 3;

    protected override string Prefix => @"Local\acpmf_";
    public override string Game => "Assetto Corsa Rally / ACC / AC";

    protected override Sample? Build()
    {
        var p = Physics!;
        // ACC and AC1 report replay/pause here. AC Rally leaves the graphics page blank
        // (status 0), so only an explicit replay or pause turns the lights off.
        int status = Graphics?.Read<int>(4) ?? AcLive;
        if (status is AcReplay or AcPause) return null;
        double? top = Plausible(Static?.Read<int>(412) ?? 0) ?? CurrentMaxRpm(p);
        return new Sample(Game)
        {
            Rpm = Math.Max(0, p.Read<int>(20)),
            MaxRpm = top,
            PitLimiter = p.Read<int>(248) == 1,
            HasFlags = false,
        };
    }

    /// <summary>physics.currentMaxRpm: an int in ACC, a float in some AC Rally builds.</summary>
    internal static double? CurrentMaxRpm(IMemoryBlock p) =>
        Plausible(p.Read<int>(588)) ?? Plausible(p.Read<float>(588));

    private static double? Plausible(double rpm) => rpm is >= 1000 and <= 30000 ? rpm : null;
}
