namespace PitGlow.Tests;

public class TachTests
{
    private static Sample Car(double rpm, bool shiftNow = false, bool pit = false) => new("t")
    {
        Rpm = rpm, FirstRpm = 4000, ShiftRpm = 8000, BlinkRpm = 8400, MaxRpm = 8500, ShiftNow = shiftNow, PitLimiter = pit,
    };

    private static int Lit(Rgb[] colors) => colors.Count(c => c != Rgb.Off);
    private readonly TachStyle _style = new() { Smooth = false };

    [Fact]
    public void FillsProgressivelyGreenToRed()
    {
        var counts = new[] { 1000, 4000, 5000, 6000, 7000, 7900 }.Select(r => Lit(Effects.TachColors(8, Car(r), _style, 0))).ToList();
        Assert.Equal(counts.OrderBy(x => x), counts);
        Assert.Equal(0, counts[0]);
        Assert.Equal(7, counts[^1]);
        var full = Effects.TachColors(8, Car(7999), _style, 0);
        Assert.Equal(new Rgb(0, 255, 0), full[0]);
        Assert.Equal(Rgb.Off, full[7]);
        var almost = Effects.TachColors(8, Car(7999.9), new TachStyle { Smooth = true }, 0);
        Assert.Equal(0, almost[7].G);
        Assert.Equal(0, almost[7].B);
    }

    [Fact]
    public void ShiftPointAllRedThenBlinks()
    {
        Assert.All(Effects.TachColors(8, Car(8100), _style, 0), c => Assert.Equal(new Rgb(255, 0, 0), c));
        Assert.All(Effects.TachColors(8, Car(8450), _style, 0.01), c => Assert.Equal(new Rgb(255, 0, 0), c));
        Assert.All(Effects.TachColors(8, Car(8450), _style, 0.07), c => Assert.Equal(Rgb.Off, c));
    }

    [Fact]
    public void GameShiftSignal() =>
        Assert.All(Effects.TachColors(8, Car(5000, shiftNow: true), _style, 0), c => Assert.Equal(new Rgb(255, 0, 0), c));

    [Fact]
    public void StartLedAndDirection()
    {
        var s = Car(4000 + 4000 * 2.5 / 8);   // two LEDs lit
        Assert.Equal(2, Lit(Effects.TachColors(8, s, _style, 0)));
        var cw = Effects.TachColors(8, s, _style, 0, startLed: 5);
        Assert.NotEqual(Rgb.Off, cw[5]);
        Assert.NotEqual(Rgb.Off, cw[6]);
        Assert.Equal(Rgb.Off, cw[4]);
        var ccw = Effects.TachColors(8, s, _style, 0, startLed: 5, reverse: true);
        Assert.NotEqual(Rgb.Off, ccw[5]);
        Assert.NotEqual(Rgb.Off, ccw[4]);
        Assert.Equal(Rgb.Off, ccw[6]);
    }

    [Fact]
    public void SmoothLeadingLed()
    {
        var colors = Effects.TachColors(8, Car(4000 + 4000 * 2.5 / 8), new TachStyle { Smooth = true }, 0);
        Assert.Equal(3, Lit(colors));
        Assert.True(colors[2].Max < colors[1].Max);
    }

    [Fact]
    public void PitLimiterAlternatesHalves()
    {
        var a = Effects.TachColors(8, Car(3000, pit: true), _style, 0.0);
        var b = Effects.TachColors(8, Car(3000, pit: true), _style, 0.3);
        Assert.Equal(4, Lit(a));
        Assert.Equal(4, Lit(b));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void FallbacksWithoutCarData()
    {
        var (first, shift, blink) = Effects.GaugePoints(new Sample("t"), new TachStyle(), null);
        Assert.True(first < shift);
        Assert.True(shift <= blink);
        (_, shift, _) = Effects.GaugePoints(new Sample("t") { MaxRpm = 7000 }, new TachStyle(), null);
        Assert.Equal(7000 * 0.93, shift, 6);
        Assert.All(Effects.TachColors(8, new Sample("t") { Rpm = double.NaN }, _style, 0), c => Assert.Equal(Rgb.Off, c));
    }

    [Fact]
    public void Brightness() =>
        Assert.Equal(new Rgb(128, 0, 0), Effects.TachColors(8, Car(8100), new TachStyle { Brightness = 0.5 }, 0)[0]);
}

public class FlagTests
{
    [Fact]
    public void Patterns()
    {
        var yellow = Effects.DefaultFlagStyles[Flag.Yellow];
        Assert.All(Effects.FlagColors(yellow, 3, 0.0), c => Assert.Equal(new Rgb(255, 170, 0), c));
        Assert.All(Effects.FlagColors(yellow, 3, 0.3), c => Assert.Equal(Rgb.Off, c));
        var blue = Effects.DefaultFlagStyles[Flag.Blue];
        Assert.NotEqual(Effects.FlagColors(blue, 1, 0.0), Effects.FlagColors(blue, 1, 0.5));
        var checkered = Effects.DefaultFlagStyles[Flag.Checkered];
        var a = Effects.FlagColors(checkered, 4, 0.0);
        var b = Effects.FlagColors(checkered, 4, 0.3);
        Assert.Equal(a[0], b[1]);
        Assert.NotEqual(a[0], a[1]);
        Assert.NotEqual(Effects.FlagColors(checkered, 1, 0.0), Effects.FlagColors(checkered, 1, 0.3));   // one-LED devices still alternate
        Assert.All(Effects.FlagColors(new FlagStyle(FlagPattern.Solid, [new(200, 100, 0)], 0), 2, 0, 0.5),
                   c => Assert.Equal(new Rgb(100, 50, 0), c));
        Assert.Throws<ConfigException>(() => FlagStyle.ParsePattern("sparkle"));
        Assert.Throws<ConfigException>(() => new FlagStyle(FlagPattern.Solid, [], 0));
    }

    [Fact]
    public void EveryFlagHasAStyleAndPriority()
    {
        Assert.True(Enum.GetValues<Flag>().ToHashSet().SetEquals(Effects.DefaultFlagStyles.Keys));
        Assert.True(Enum.GetValues<Flag>().ToHashSet().SetEquals(Effects.DefaultPriority));
        Assert.Equal(Enum.GetValues<Flag>().Length, Effects.DefaultPriority.Count);
    }

    [Fact]
    public void FlagNamesRoundTrip()
    {
        foreach (var f in Enum.GetValues<Flag>()) Assert.Equal(f, Flags.Parse(f.ConfigName()));
        Assert.Equal("FULL_COURSE_YELLOW", Flag.FullCourseYellow.ConfigName());
        Assert.Equal("BLACK_WHITE", Flag.BlackWhite.ConfigName());
    }

    [Fact]
    public void Priority()
    {
        var sel = new FlagSelector(Effects.DefaultPriority, Effects.DefaultFlagStyles);
        Assert.Equal(Flag.Yellow, sel.Update([Flag.Blue, Flag.Yellow], 0));
        Assert.Equal(Flag.Red, sel.Update([Flag.Blue, Flag.Yellow, Flag.Red], 0.1));
    }

    [Fact]
    public void GreenOnlyShownForHoldTime()
    {
        var sel = new FlagSelector(Effects.DefaultPriority, Effects.DefaultFlagStyles);
        var shown = new Dictionary<int, Flag?>();
        for (int t = 0; t < 100; t++) shown[t] = sel.Update([Flag.Green], t / 10.0);
        Assert.Equal(Flag.Green, shown[0]);
        Assert.Equal(Flag.Green, shown[59]);
        Assert.Null(shown[65]);                            // still set by the sim all race
        Assert.Null(sel.Update([], 7));
        Assert.Null(sel.Update([], 20));
        Assert.Equal(Flag.Green, sel.Update([Flag.Green], 30));   // a new green (restart)
    }

    [Fact]
    public void LingerPreventsFlicker()
    {
        var sel = new FlagSelector(Effects.DefaultPriority, Effects.DefaultFlagStyles, linger: 1.0);
        sel.Update([Flag.Blue], 0);
        Assert.Equal(Flag.Blue, sel.Update([], 0.5));
        Assert.Null(sel.Update([], 1.6));
    }

    [Fact]
    public void HexColors()
    {
        Assert.Equal(new Rgb(0x0A, 0xBC, 0xFF), Rgb.FromHex("#0abcff"));
        Assert.Equal("#0ABCFF", new Rgb(0x0A, 0xBC, 0xFF).ToHex());
        Assert.Throws<ConfigException>(() => Rgb.FromHex("red"));
    }
}
