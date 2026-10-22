using System.Globalization;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace PitGlow.Config;

/// <summary>pitglow.toml: load, validate and write (with comments) the user's settings.</summary>
public static class SettingsFile
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static Settings Load(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    public static Settings Parse(string text)
    {
        TomlTable data;
        try
        {
            data = Toml.ToModel(text);
        }
        catch (TomlException ex)
        {
            throw new ConfigException($"pitglow.toml could not be read: {ex.Message}", ex);
        }

        var cfg = new Settings();
        var rgb = Table(data, "openrgb");
        cfg.Host = Str(rgb, "host", cfg.Host);
        cfg.Port = Int(rgb, "port", cfg.Port);
        cfg.OpenRgbEnabled = Bool(rgb, "enabled", true);
        cfg.ICueEnabled = Bool(Table(data, "icue"), "enabled", true);
        cfg.RazerEnabled = Bool(Table(data, "razer"), "enabled", true);

        var game = Table(data, "game");
        cfg.Game = new GameCfg
        {
            Source = Str(game, "source", "auto"),
            Wreckfest2Port = Int(game, "wreckfest2_port", 23123),
            BeamngPort = Int(game, "beamng_port", 4444),
        };
        if (!GameCfg.Sources.Contains(cfg.Game.Source))
            throw new ConfigException($"[game] source must be one of {string.Join(", ", GameCfg.Sources)}");

        var t = Table(data, "tach");
        var d = new TachStyle();
        var gradient = d.Gradient;
        if (t.TryGetValue("gradient", out var g))
        {
            gradient = [.. ((TomlArray)g).Select(stop =>
            {
                var pair = (TomlArray)stop!;
                return new GradientStop(Num(pair[0]), Rgb.FromHex((string)pair[1]!));
            })];
            if (gradient.Count < 1) throw new ConfigException("[tach] gradient needs at least one stop");
        }
        cfg.Tach = new TachStyle
        {
            Gradient = gradient,
            ShiftColor = Rgb.FromHex(Str(t, "shift_color", d.ShiftColor.ToHex())),
            BlinkHz = Dbl(t, "blink_hz", d.BlinkHz),
            Brightness = Dbl(t, "brightness", d.Brightness),
            Smooth = Bool(t, "smooth", d.Smooth),
            StartPct = Dbl(t, "start_pct", d.StartPct),
            ShiftPct = Dbl(t, "shift_pct", d.ShiftPct),
            BlinkPct = Dbl(t, "blink_pct", d.BlinkPct),
            PitLimiter = Bool(t, "pit_limiter", d.PitLimiter),
            LimiterColor = Rgb.FromHex(Str(t, "limiter_color", d.LimiterColor.ToHex())),
            LimiterHz = Dbl(t, "limiter_hz", d.LimiterHz),
        };

        var f = Table(data, "flags");
        cfg.FlagBrightness = Dbl(f, "brightness", 1.0);
        cfg.WhenClear = Str(f, "when_clear", "original");
        if (cfg.WhenClear is not ("original" or "off")) Rgb.FromHex(cfg.WhenClear);
        cfg.LingerSeconds = Dbl(f, "linger_seconds", 1.0);
        if (f.TryGetValue("priority", out var pr))
            cfg.Priority = [.. ((TomlArray)pr).Select(x => Flags.Parse((string)x!))];
        foreach (var (name, value) in Table(f, "style"))
        {
            var flag = Flags.Parse(name);
            var s = (TomlTable)value;
            var b = Effects.DefaultFlagStyles[flag];
            cfg.FlagStyles[flag] = new FlagStyle(
                s.TryGetValue("pattern", out var p) ? FlagStyle.ParsePattern((string)p) : b.Pattern,
                s.TryGetValue("colors", out var c) ? [.. ((TomlArray)c).Select(x => Rgb.FromHex((string)x!))] : b.Colors,
                Dbl(s, "hz", b.Hz),
                Dbl(s, "hold_seconds", b.HoldSeconds));
        }

        if (data.TryGetValue("device", out var devices))
        {
            int i = 0;
            foreach (var dev in (TomlTableArray)devices)
            {
                i++;
                var name = Str(dev, "name", "");
                var where = $"device #{i} ({name})";
                if (name.Length == 0) throw new ConfigException($"{where}: every device needs a name");
                var zones = new List<ZoneCfg>();
                if (dev.TryGetValue("zone", out var zs))
                {
                    foreach (var z in (TomlTableArray)zs)
                    {
                        zones.Add(new ZoneCfg
                        {
                            Zone = Str(z, "zone", ""),
                            Role = Roles.Check(Str(z, "role", Roles.Ignore), where),
                            StartLed = Int(z, "start_led", 0),
                            Reverse = Bool(z, "reverse", false),
                            Index = z.ContainsKey("index") ? Int(z, "index", 0) : null,
                        });
                    }
                }
                cfg.Devices.Add(new DeviceCfg
                {
                    Name = name,
                    Backend = Str(dev, "backend", "openrgb"),
                    Index = Int(dev, "index", 0),
                    Role = dev.ContainsKey("role") ? Roles.Check(Str(dev, "role", ""), where) : null,
                    MaxFps = Dbl(dev, "max_fps", 30),
                    Location = dev.ContainsKey("location") ? Str(dev, "location", "") : null,
                    Zones = zones,
                });
            }
        }
        return cfg;
    }

    // ---- tolerant accessors -----------------------------------------------

    private static TomlTable Table(TomlTable t, string key) =>
        t.TryGetValue(key, out var v) && v is TomlTable table ? table : new TomlTable();

    private static double Num(object? v) => v switch
    {
        long l => l,
        double d => d,
        int i => i,
        _ => throw new ConfigException($"Expected a number, got \"{v}\""),
    };

    private static double Dbl(TomlTable t, string key, double fallback) =>
        t.TryGetValue(key, out var v) ? Num(v) : fallback;

    private static int Int(TomlTable t, string key, int fallback) =>
        t.TryGetValue(key, out var v) ? (int)Num(v) : fallback;

    private static bool Bool(TomlTable t, string key, bool fallback) =>
        t.TryGetValue(key, out var v) ? v is bool b ? b : throw new ConfigException($"{key} must be true or false") : fallback;

    private static string Str(TomlTable t, string key, string fallback) =>
        t.TryGetValue(key, out var v) ? v as string ?? throw new ConfigException($"{key} must be text in quotes") : fallback;

    // ---- writing ---------------------------------------------------------------

    private static string S(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20) sb.Append($"\\u{(int)ch:X4}");
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    private static string B(bool v) => v ? "true" : "false";

    /// <summary>Numbers the way Python printed them: 8.0, 0.55, 1.0.</summary>
    private static string N(double v) =>
        v == Math.Floor(v) && Math.Abs(v) < 1e15 ? v.ToString("0.0", Inv) : v.ToString("R", Inv);

    private static string PatternName(FlagPattern p) => p.ToString().ToLowerInvariant();

    public static string ToToml(Settings cfg)
    {
        var t = cfg.Tach;
        var lines = new List<string>
        {
            "# Potato PitGlow settings. Edit, save, and restart PitGlow.",
            "# Colors are #RRGGBB. Brightness is 0.0 - 1.0. hz = flashes per second.",
            "",
            "# Lighting routes. Each device is driven by exactly one of them.",
            "[openrgb]",
            "# OpenRGB > SDK Server tab must be running. Optional: PitGlow carries on without it.",
            $"enabled = {B(cfg.OpenRgbEnabled)}",
            $"host = {S(cfg.Host)}",
            $"port = {cfg.Port}",
            "",
            "[icue]",
            "# Corsair devices through Corsair iCUE (iCUE must be installed and running).",
            $"enabled = {B(cfg.ICueEnabled)}",
            "",
            "[razer]",
            "# Razer devices through Razer Synapse (Chroma).",
            $"enabled = {B(cfg.RazerEnabled)}",
            "",
            "[game]",
            "# auto | iracing | acevo | acrally | wreckfest2 | beamng | demo",
            $"source = {S(cfg.Game.Source)}",
            $"wreckfest2_port = {cfg.Game.Wreckfest2Port}",
            $"beamng_port = {cfg.Game.BeamngPort}",
            "",
            "[tach]",
            "# Color of each LED by its position on the dial (0.0 = first LED, 1.0 = last).",
            "gradient = [" + string.Join(", ", t.Gradient.Select(s => $"[{N(s.Position)}, {S(s.Color.ToHex())}]")) + "]",
            "# Whole ring turns this color at the shift point, then flashes at blink_hz.",
            $"shift_color = {S(t.ShiftColor.ToHex())}",
            $"blink_hz = {N(t.BlinkHz)}",
            $"brightness = {N(t.Brightness)}",
            "# Fade the leading LED in, for a smoother needle.",
            $"smooth = {B(t.Smooth)}",
            "# Used only when a game does not provide the car's shift-light points (% of max RPM).",
            $"start_pct = {N(t.StartPct)}",
            $"shift_pct = {N(t.ShiftPct)}",
            $"blink_pct = {N(t.BlinkPct)}",
            "# Pit limiter on: fans alternate halves in limiter_color.",
            $"pit_limiter = {B(t.PitLimiter)}",
            $"limiter_color = {S(t.LimiterColor.ToHex())}",
            $"limiter_hz = {N(t.LimiterHz)}",
            "",
            "[flags]",
            $"brightness = {N(cfg.FlagBrightness)}",
            "# What flag devices show when no flag is out: original | off | #RRGGBB",
            $"when_clear = {S(cfg.WhenClear)}",
            "# Keep a flag up this long after it clears, to avoid flicker.",
            $"linger_seconds = {N(cfg.LingerSeconds)}",
            "# When several flags are out, the first one in this list wins.",
            "priority = [" + string.Join(", ", cfg.Priority.Select(f => S(f.ConfigName()))) + "]",
            "",
            "# pattern: solid | flash | pulse | swap (neighbouring LEDs alternate two colors)",
            "# hold_seconds: stop showing the flag this long after it appears (0 = while active)",
        };
        foreach (var flag in Enum.GetValues<Flag>())
        {
            if (!cfg.FlagStyles.TryGetValue(flag, out var s)) continue;
            lines.AddRange([
                $"[flags.style.{flag.ConfigName()}]",
                $"pattern = {S(PatternName(s.Pattern))}",
                "colors = [" + string.Join(", ", s.Colors.Select(c => S(c.ToHex()))) + "]",
                $"hz = {N(s.Hz)}",
                $"hold_seconds = {N(s.HoldSeconds)}",
                "",
            ]);
        }
        lines.AddRange([
            "# ---------------------------------------------------------------------------",
            "# Devices. role: tach (RPM gauge) | flags | ignore",
            "# A device with a role uses all its LEDs; otherwise each zone has its own role.",
            "# start_led / reverse: where the gauge starts on each fan and which way it sweeps.",
            "# location / index: how PitGlow finds the device and zone again if names change.",
            "# max_fps: cap on updates per second for that device.",
            "# ---------------------------------------------------------------------------",
        ]);
        foreach (var dev in cfg.Devices)
        {
            lines.AddRange(["", "[[device]]", $"name = {S(dev.Name)}", $"backend = {S(dev.Backend)}", $"index = {dev.Index}"]);
            if (!string.IsNullOrEmpty(dev.Location)) lines.Add($"location = {S(dev.Location)}");
            if (dev.Role is not null) lines.Add($"role = {S(dev.Role)}");
            lines.Add($"max_fps = {N(dev.MaxFps)}");
            foreach (var z in dev.Zones)
            {
                lines.AddRange(["", "  [[device.zone]]", $"  zone = {S(z.Zone)}"]);
                if (z.Index is int zi) lines.Add($"  index = {zi}");
                lines.Add($"  role = {S(z.Role)}");
                if (z.Role == Roles.Tach) lines.AddRange([$"  start_led = {z.StartLed}", $"  reverse = {B(z.Reverse)}"]);
            }
        }
        return string.Join("\n", lines) + "\n";
    }

    public static void Save(Settings cfg, string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, ToToml(cfg), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
