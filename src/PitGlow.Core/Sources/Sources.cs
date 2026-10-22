using PitGlow.Config;

namespace PitGlow.Sources;

/// <summary>A game's telemetry. Read returns null while the player isn't driving.</summary>
public interface ITelemetrySource
{
    string Game { get; }
    Sample? Read(double now);
    void Close();
    string? Error => null;
}

/// <summary>Watches every supported game and follows whichever one is driving.</summary>
public sealed class AutoSource(IReadOnlyList<ITelemetrySource> sources) : ITelemetrySource
{
    private double _nextScan;

    public ITelemetrySource? Active { get; private set; }
    public IReadOnlyList<ITelemetrySource> Sources => sources;
    public string Game => Active?.Game ?? "waiting for a game";

    public IEnumerable<string> Errors() => sources.Select(s => s.Error).OfType<string>();

    public Sample? Read(double now)
    {
        if (Active is not null)
        {
            var sample = Active.Read(now);
            if (sample is not null) return sample;
            Active = null;
        }
        if (now < _nextScan) return null;
        _nextScan = now + 0.5;
        foreach (var source in sources)
        {
            var sample = source.Read(now);
            if (sample is not null)
            {
                Active = source;
                return sample;
            }
        }
        return null;
    }

    public void Close()
    {
        foreach (var s in sources) s.Close();
    }
}

/// <summary>Fake car: revs through the gears while cycling every flag.</summary>
public sealed class DemoSource : ITelemetrySource
{
    private readonly double _flagSeconds;
    private readonly IReadOnlyList<Flag?> _flags;
    private double? _start;

    public DemoSource(double flagSeconds = 4.0, IReadOnlyList<Flag?>? flags = null)
    {
        _flagSeconds = flagSeconds;
        if (flags is null)
        {
            var all = new List<Flag?> { null };
            all.AddRange(Effects.DefaultPriority.Reverse().Select(f => (Flag?)f));
            flags = all;
        }
        _flags = flags;
    }

    public string Game => "demo";

    public Flag? CurrentFlag(double now)
    {
        int index = (int)Math.Floor((now - (_start ?? now)) / _flagSeconds) % _flags.Count;
        return _flags[index];
    }

    public Sample Read(double now)
    {
        _start ??= now;
        double t = now - _start.Value;
        double gearT = Effects.PyMod(t, 2.5) / 2.5;                 // one "gear" every 2.5 s
        double rpm = 3500 + (8600 - 3500) * Math.Sin(gearT * Math.PI / 2);
        var flag = CurrentFlag(now);
        double minute = Effects.PyMod(t, 60);
        return new Sample(Game)
        {
            Rpm = rpm, FirstRpm = 4500, ShiftRpm = 8000, BlinkRpm = 8400, MaxRpm = 8500,
            PitLimiter = minute >= 12 && minute < 15,
            Flags = flag is Flag f ? new HashSet<Flag> { f } : Flags.None,
        };
    }

    Sample? ITelemetrySource.Read(double now) => Read(now);

    public void Close() { }
}

public static class SourceFactory
{
    public static ITelemetrySource Make(GameCfg game)
    {
        if (game.Source == "demo") return new DemoSource();
        var table = new Dictionary<string, Func<ITelemetrySource>>
        {
            ["iracing"] = () => new IRacingSource(),
            ["acevo"] = () => new ACEvoSource(),
            ["acrally"] = () => new ACClassicSource(),
            ["wreckfest2"] = () => new Wreckfest2Source(game.Wreckfest2Port),
            ["beamng"] = () => new BeamNGSource(game.BeamngPort),
        };
        if (game.Source == "auto") return new AutoSource([.. table.Values.Select(make => make())]);
        if (!table.TryGetValue(game.Source, out var one))
            throw new ConfigException($"Unknown game source \"{game.Source}\"; use one of {string.Join(", ", GameCfg.Sources)}");
        return new AutoSource([one()]);
    }
}
