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
    private int? _lastPacket;
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

    /// <summary>Physics packetId must keep moving; a closed game leaves stale data.</summary>
    private bool Fresh(double now)
    {
        int packet = Physics!.Read<int>(0);
        if (_lastPacket is null)   // need to see it move at least once
        {
            _lastPacket = packet;
            _lastChange = now;
            return false;
        }
        if (packet != _lastPacket)
        {
            _lastPacket = packet;
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
    protected override string Prefix => @"Local\acpmf_";
    public override string Game => "Assetto Corsa Rally / ACC / AC";

    protected override Sample? Build()
    {
        var p = Physics!;
        if (Graphics is not null && Graphics.Read<int>(4) != AcLive) return null;
        int top = Static?.Read<int>(412) ?? 0;                  // maxRpm
        return new Sample(Game)
        {
            Rpm = p.Read<int>(20),
            MaxRpm = top > 0 ? top : null,
            PitLimiter = p.Read<int>(248) != 0,
        };
    }
}
