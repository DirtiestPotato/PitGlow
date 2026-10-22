namespace PitGlow;

public sealed record GradientStop(double Position, Rgb Color);

/// <summary>How the RPM gauge looks.</summary>
public sealed record TachStyle
{
    public static readonly IReadOnlyList<GradientStop> DefaultGradient =
    [
        new(0.0, new Rgb(0, 255, 0)), new(0.55, new Rgb(255, 255, 0)),
        new(0.8, new Rgb(255, 90, 0)), new(1.0, new Rgb(255, 0, 0)),
    ];

    public IReadOnlyList<GradientStop> Gradient { get; init; } = DefaultGradient;
    public Rgb ShiftColor { get; init; } = new(255, 0, 0);
    public double BlinkHz { get; init; } = 8.0;
    public double Brightness { get; init; } = 1.0;
    public bool Smooth { get; init; } = true;
    /// <summary>true: fill from idle to the shift point like a tach; false: only across the car's shift lights.</summary>
    public bool FullRange { get; init; } = true;
    /// <summary>Full range without a known idle RPM: start at this share of the shift point.</summary>
    public double FullRangeStartPct { get; init; } = 0.30;
    public double StartPct { get; init; } = 0.60;   // of max RPM, used when the game gives no shift lights
    public double ShiftPct { get; init; } = 0.93;
    public double BlinkPct { get; init; } = 0.98;
    public bool PitLimiter { get; init; } = true;
    public Rgb LimiterColor { get; init; } = new(0, 80, 255);
    public double LimiterHz { get; init; } = 2.0;

    public bool SameAs(TachStyle o) =>
        Gradient.SequenceEqual(o.Gradient) && ShiftColor == o.ShiftColor && BlinkHz == o.BlinkHz &&
        Brightness == o.Brightness && Smooth == o.Smooth && FullRange == o.FullRange &&
        FullRangeStartPct == o.FullRangeStartPct && StartPct == o.StartPct &&
        ShiftPct == o.ShiftPct && BlinkPct == o.BlinkPct && PitLimiter == o.PitLimiter &&
        LimiterColor == o.LimiterColor && LimiterHz == o.LimiterHz;
}

public enum FlagPattern { Solid, Flash, Pulse, Swap }

/// <summary>How one flag is shown on the flag devices.</summary>
public sealed record FlagStyle
{
    public FlagStyle(FlagPattern pattern, IReadOnlyList<Rgb> colors, double hz, double holdSeconds = 0.0)
    {
        if (colors.Count == 0) throw new ConfigException("A flag style needs at least one color");
        Pattern = pattern;
        Colors = colors;
        Hz = hz;
        HoldSeconds = holdSeconds;
    }

    public FlagPattern Pattern { get; init; }
    public IReadOnlyList<Rgb> Colors { get; init; }
    public double Hz { get; init; }
    public double HoldSeconds { get; init; }   // 0 = show for as long as the game reports it

    public static FlagPattern ParsePattern(string text) => text.Trim().ToLowerInvariant() switch
    {
        "solid" => FlagPattern.Solid,
        "flash" => FlagPattern.Flash,
        "pulse" => FlagPattern.Pulse,
        "swap" => FlagPattern.Swap,
        _ => throw new ConfigException($"Unknown flag pattern \"{text}\"; use solid, flash, pulse or swap"),
    };

    public bool SameAs(FlagStyle o) =>
        Pattern == o.Pattern && Colors.SequenceEqual(o.Colors) && Hz == o.Hz && HoldSeconds == o.HoldSeconds;
}

/// <summary>Pure lighting math: RPM gauge and flag patterns. No hardware access.</summary>
public static class Effects
{
    public static readonly IReadOnlyDictionary<Flag, FlagStyle> DefaultFlagStyles = new Dictionary<Flag, FlagStyle>
    {
        [Flag.Green] = new(FlagPattern.Flash, [new(0, 255, 0)], 2.0, holdSeconds: 6.0),
        [Flag.Yellow] = new(FlagPattern.Flash, [new(255, 170, 0)], 2.0),
        [Flag.FullCourseYellow] = new(FlagPattern.Flash, [new(255, 170, 0)], 4.0),
        [Flag.Blue] = new(FlagPattern.Pulse, [new(0, 60, 255)], 1.0),
        [Flag.White] = new(FlagPattern.Solid, [new(255, 255, 255)], 0.0),
        [Flag.Checkered] = new(FlagPattern.Swap, [new(255, 255, 255), Rgb.Off], 2.0),
        [Flag.Red] = new(FlagPattern.Solid, [new(255, 0, 0)], 0.0),
        [Flag.Black] = new(FlagPattern.Swap, [new(255, 255, 255), new(255, 0, 0)], 1.5),
        [Flag.BlackWhite] = new(FlagPattern.Swap, [new(255, 255, 255), Rgb.Off], 1.0),
        [Flag.Meatball] = new(FlagPattern.Pulse, [new(255, 70, 0)], 1.0),
        [Flag.Disqualified] = new(FlagPattern.Flash, [new(255, 0, 0)], 5.0),
        [Flag.Debris] = new(FlagPattern.Swap, [new(255, 0, 0), new(255, 200, 0)], 2.0),
    };

    public static readonly IReadOnlyList<Flag> DefaultPriority =
    [
        Flag.Red, Flag.Disqualified, Flag.Black, Flag.Meatball, Flag.FullCourseYellow,
        Flag.Yellow, Flag.BlackWhite, Flag.Checkered, Flag.Debris, Flag.Blue,
        Flag.White, Flag.Green,
    ];

    /// <summary>Python-style modulo: never negative for a positive divisor.</summary>
    public static double PyMod(double a, double b) => a - b * Math.Floor(a / b);
    public static int PyMod(int a, int b) => ((a % b) + b) % b;

    /// <summary>True for the "on" half of a square wave.</summary>
    public static bool Square(double now, double hz) => hz <= 0 || PyMod(now * hz, 1.0) < 0.5;

    public static Rgb GradientAt(IReadOnlyList<GradientStop> stops, double p)
    {
        if (p <= stops[0].Position) return stops[0].Color;
        for (int i = 0; i + 1 < stops.Count; i++)
        {
            var (p0, c0) = (stops[i].Position, stops[i].Color);
            var (p1, c1) = (stops[i + 1].Position, stops[i + 1].Color);
            if (p <= p1) return Rgb.Lerp(c0, c1, p1 > p0 ? (p - p0) / (p1 - p0) : 1);
        }
        return stops[^1].Color;
    }

    private static double? Truthy(double? v) => v is double d && d != 0 ? d : null;

    /// <summary>Return (first, shift, blink) RPM for the gauge.</summary>
    public static (double First, double Shift, double Blink) GaugePoints(Sample s, TachStyle style, double? learnedMax)
    {
        double top = Truthy(s.MaxRpm) ?? Truthy(learnedMax) ?? 8000.0;
        double shift = Truthy(s.ShiftRpm) ?? top * style.ShiftPct;
        double first = style.FullRange
            ? Truthy(s.IdleRpm) ?? shift * style.FullRangeStartPct
            : Truthy(s.FirstRpm) ?? top * style.StartPct;
        if (first >= shift) first = shift * (style.FullRange ? style.FullRangeStartPct : 0.6);
        double blink = Truthy(s.BlinkRpm) ?? (top * style.BlinkPct > shift ? top * style.BlinkPct : shift);
        return (first, shift, Math.Max(blink, shift));
    }

    /// <summary>True while the car is at or past its shift point (and not on the pit limiter).</summary>
    public static bool ShiftActive(Sample s, TachStyle style, double? learnedMax)
    {
        if (s.PitLimiter && style.PitLimiter) return false;
        double rpm = double.IsFinite(s.Rpm) ? s.Rpm : 0.0;
        return s.ShiftNow || rpm >= GaugePoints(s, style, learnedMax).Shift;
    }

    /// <summary>Colors for one ring or strip of n LEDs, in the device's LED order.</summary>
    public static Rgb[] TachColors(int n, Sample s, TachStyle style, double now,
                                   int startLed = 0, bool reverse = false, double? learnedMax = null)
    {
        if (n <= 0) return [];
        var gauge = new Rgb[n];   // in gauge order: 0 = start of the dial; default = Off
        double rpm = double.IsFinite(s.Rpm) ? s.Rpm : 0.0;
        var (first, shift, blink) = GaugePoints(s, style, learnedMax);
        if (s.PitLimiter && style.PitLimiter)
        {
            bool phase = Square(now, style.LimiterHz);
            int half = n / 2;
            for (int k = 0; k < n; k++) gauge[k] = (k < half) == phase ? style.LimiterColor : Rgb.Off;
        }
        else if (rpm >= blink && blink > shift)
        {
            var c = Square(now, style.BlinkHz) ? style.ShiftColor : Rgb.Off;
            Array.Fill(gauge, c);
        }
        else if (rpm >= shift || s.ShiftNow)
        {
            Array.Fill(gauge, style.ShiftColor);
        }
        else
        {
            double lit = Math.Clamp((rpm - first) / (shift - first), 0.0, 1.0) * n;
            int whole = (int)lit;
            for (int k = 0; k < n; k++)
            {
                var color = GradientAt(style.Gradient, n > 1 ? (double)k / (n - 1) : 1.0);
                if (k < whole) gauge[k] = color;
                else if (k == whole && style.Smooth) gauge[k] = color.Scale(lit - whole);
            }
        }
        var output = new Rgb[n];
        for (int k = 0; k < n; k++)
            output[PyMod(startLed + (reverse ? -k : k), n)] = gauge[k].Scale(style.Brightness);
        return output;
    }

    public static Rgb[] FlagColors(FlagStyle style, int n, double now, double brightness = 1.0)
    {
        if (n <= 0) return [];
        var first = style.Colors[0];
        var output = new Rgb[n];
        switch (style.Pattern)
        {
            case FlagPattern.Solid:
                Array.Fill(output, first);
                break;
            case FlagPattern.Flash:
                Array.Fill(output, Square(now, style.Hz) ? first : Rgb.Off);
                break;
            case FlagPattern.Pulse:
                double level = 0.15 + 0.85 * (0.5 - 0.5 * Math.Cos(2 * Math.PI * now * style.Hz));
                Array.Fill(output, first.Scale(level));
                break;
            default:
                // swap: neighbouring LEDs alternate colors and trade places at hz.
                // On a single-LED device this alternates the whole device over time.
                IReadOnlyList<Rgb> colors = style.Colors.Count > 1 ? style.Colors : [first, Rgb.Off];
                int phase = Square(now, style.Hz) ? 0 : 1;
                for (int i = 0; i < n; i++) output[i] = colors[(i + phase) % colors.Count];
                break;
        }
        for (int i = 0; i < n; i++) output[i] = output[i].Scale(brightness);
        return output;
    }
}

/// <summary>
/// Picks the one flag to display from whatever the game reports.
/// Priority decides between simultaneous flags; hold_seconds limits how long a flag is
/// shown after it appears (iRacing may keep green set all race); linger keeps a flag up
/// briefly after it clears so the lights do not flicker.
/// </summary>
public sealed class FlagSelector(IReadOnlyList<Flag> priority, IReadOnlyDictionary<Flag, FlagStyle> styles, double linger = 1.0)
{
    private readonly Dictionary<Flag, double> _since = [];
    private readonly Dictionary<Flag, double> _lastSeen = [];

    public void Reset()
    {
        _since.Clear();
        _lastSeen.Clear();
    }

    public Flag? Update(IEnumerable<Flag> flags, double now)
    {
        foreach (var flag in flags)
        {
            if (!_since.ContainsKey(flag) || now - _lastSeen.GetValueOrDefault(flag, now) > linger)
                _since[flag] = now;
            _lastSeen[flag] = now;
        }
        foreach (var flag in priority)
        {
            if (!_lastSeen.TryGetValue(flag, out var seen) || now - seen > linger) continue;
            if (!styles.TryGetValue(flag, out var style)) continue;
            if (style.HoldSeconds > 0 && now - _since[flag] > style.HoldSeconds) continue;
            return flag;
        }
        return null;
    }
}
