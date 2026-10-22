using PitGlow.Config;

namespace PitGlow.Rendering;

/// <summary>Turns a telemetry sample into one full color frame per device.</summary>
public sealed class Renderer(Settings cfg)
{
    private readonly FlagSelector _selector = new(cfg.Priority, cfg.FlagStyles, cfg.LingerSeconds);

    public double? LearnedMax { get; private set; }
    public Flag? Flag { get; private set; }

    public void Reset()
    {
        _selector.Reset();
        LearnedMax = null;
        Flag = null;
    }

    public void Update(Sample sample, double now)
    {
        if (sample.MaxRpm is null && sample.Rpm > 0)
            // Games without a max RPM (BeamNG): learn it from the highest RPM seen.
            LearnedMax = Math.Max(LearnedMax ?? 0.0, sample.Rpm);
        Flag = _selector.Update(sample.Flags, now);
    }

    public Rgb[] Frame(Layout layout, Sample sample, double now)
    {
        var output = layout.Info.OriginalColors.ToArray();   // unassigned LEDs keep their original color
        bool shifting = layout.Groups.Any(g => g.ShiftAlert) && Effects.ShiftActive(sample, cfg.Tach, LearnedMax);
        foreach (var g in layout.Groups)
        {
            int n = g.Leds.Count;
            Rgb[] colors;
            if (g.Role == Roles.Tach)
                colors = Effects.TachColors(n, sample, cfg.Tach, now, g.StartLed, g.Reverse, LearnedMax);
            else if (shifting && g.ShiftAlert)
                colors = Effects.FlagColors(cfg.ShiftAlertStyle, n, now, cfg.FlagBrightness);
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

    private Rgb[] Clear(Layout layout, Group g) => cfg.WhenClear switch
    {
        "original" => [.. g.Leds.Select(i => layout.Info.OriginalColors[i])],
        "off" => new Rgb[g.Leds.Count],
        _ => Enumerable.Repeat(Rgb.FromHex(cfg.WhenClear), g.Leds.Count).ToArray(),
    };
}
