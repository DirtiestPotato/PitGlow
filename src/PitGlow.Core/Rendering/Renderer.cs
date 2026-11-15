using PitGlow.Config;

namespace PitGlow.Rendering;

/// <summary>Turns a telemetry sample into one full color frame per device.</summary>
public sealed class Renderer(Settings cfg)
{
    private readonly FlagSelector _selector = new(cfg.Priority, cfg.FlagStyles, cfg.LingerSeconds);

    public double? LearnedMax { get; private set; }
    public Flag? Flag { get; private set; }
    /// <summary>When the current flag appeared; all-zones mode starts its blink from here.</summary>
    public double? FlagSince { get; private set; }

    public void Reset()
    {
        _selector.Reset();
        LearnedMax = null;
        Flag = null;
        FlagSince = null;
    }

    public void Update(Sample sample, double now)
    {
        if (sample.MaxRpm is null && sample.Rpm > 0)
            // Games without a max RPM (BeamNG): learn it from the highest RPM seen.
            LearnedMax = Math.Max(LearnedMax ?? 0.0, sample.Rpm);
        Flag = _selector.Update(sample.Flags, now);
        FlagSince = Flag is PitGlow.Flag f ? _selector.SinceOf(f) : null;
    }

    public Rgb[] Frame(Layout layout, Sample sample, double now)
    {
        var output = layout.Info.OriginalColors.ToArray();   // unassigned LEDs keep their original color
        if (cfg.HueFlagsOnly && layout.Info.Backend is "hue" or "huebt") return FlagsOnlyFrame(layout, now, output);
        if (cfg.Mode == "all") return AllZonesFrame(layout, sample, now, output);
        bool shifting = layout.Groups.Any(g => g.ShiftAlert) && Effects.ShiftActive(sample, cfg.Tach, LearnedMax);
        foreach (var g in layout.Groups)
        {
            int n = g.Leds.Count;
            Rgb[] colors;
            if (g.Role == Roles.Tach)
                colors = Effects.TachColors(n, sample, cfg.Tach, now, g.StartLed, g.Reverse, LearnedMax);
            else if (shifting && g.ShiftAlert)
                colors = Effects.FlagColors(cfg.ShiftAlertStyle, n, now, cfg.FlagBrightness);
            else if (!sample.HasFlags && cfg.WhenNoFlags == "rpm")
                colors = Effects.RevColors(n, sample, cfg.Tach, now, LearnedMax);
            else if (Flag is PitGlow.Flag flag && cfg.FlagStyles.TryGetValue(flag, out var style))
                colors = Effects.FlagColors(style, n, now, cfg.FlagBrightness);
            else
                colors = Clear(layout, g);
            for (int i = 0; i < n && i < colors.Length; i++)
                if (g.Leds[i] >= 0 && g.Leds[i] < output.Length)
                    output[g.Leds[i]] = colors[i];
        }
        return output;
    }

    /// <summary>
    /// All-zones mode: every zone is an RPM gauge until a flag comes out, then every zone shows
    /// the flag. A whole-device group with several zones (two RAM sticks) gets one gauge per zone.
    /// Flags blink in step everywhere: timed from when the flag appeared, whole zones at once.
    /// </summary>
    private Rgb[] AllZonesFrame(Layout layout, Sample sample, double now, Rgb[] output)
    {
        FlagStyle? flagStyle = Flag is PitGlow.Flag flag && cfg.FlagStyles.TryGetValue(flag, out var s) ? s : null;
        double flagTime = now - (FlagSince ?? now);
        foreach (var g in layout.Groups)
        {
            if (flagStyle is not null)
            {
                Paint(output, g.Leds, Effects.FlagColors(flagStyle, g.Leds.Count, flagTime, cfg.FlagBrightness, together: true));
                continue;
            }
            var zones = layout.Cfg.Role is not null ? layout.Info.Zones.Where(z => z.Leds.Count > 0).ToList() : [];
            if (zones.Count > 1)
                foreach (var z in zones)
                    Paint(output, z.Leds, Effects.TachColors(z.Leds.Count, sample, cfg.Tach, now, learnedMax: LearnedMax));
            else
                Paint(output, g.Leds, Effects.TachColors(g.Leds.Count, sample, cfg.Tach, now, g.StartLed, g.Reverse, LearnedMax));
        }
        return output;
    }

    /// <summary>
    /// Room lights (Hue): the flag while one is out, otherwise off. No RPM gauge in any mode or
    /// game. In all-zones mode the flag blinks in step with the other devices.
    /// </summary>
    private Rgb[] FlagsOnlyFrame(Layout layout, double now, Rgb[] output)
    {
        FlagStyle? style = Flag is PitGlow.Flag flag && cfg.FlagStyles.TryGetValue(flag, out var s) ? s : null;
        bool all = cfg.Mode == "all";
        double t = all ? now - (FlagSince ?? now) : now;
        foreach (var g in layout.Groups)
            Paint(output, g.Leds, style is null
                ? new Rgb[g.Leds.Count]
                : Effects.FlagColors(style, g.Leds.Count, t, cfg.FlagBrightness, together: all));
        return output;
    }

    private static void Paint(Rgb[] output, IReadOnlyList<int> leds, Rgb[] colors)
    {
        for (int i = 0; i < leds.Count && i < colors.Length; i++)
            if (leds[i] >= 0 && leds[i] < output.Length)
                output[leds[i]] = colors[i];
    }

    /// <summary>What a device PitGlow keeps hold of shows while nobody is driving (when_clear).</summary>
    public Rgb[] IdleFrame(Layout layout)
    {
        var output = layout.Info.OriginalColors.ToArray();
        foreach (var g in layout.Groups) Paint(output, g.Leds, Clear(layout, g));
        return output;
    }

    private Rgb[] Clear(Layout layout, Group g) => cfg.WhenClear switch
    {
        "original" => [.. g.Leds.Select(i => layout.Info.OriginalColors[i])],
        "off" => new Rgb[g.Leds.Count],
        _ => Enumerable.Repeat(Rgb.FromHex(cfg.WhenClear), g.Leds.Count).ToArray(),
    };
}
