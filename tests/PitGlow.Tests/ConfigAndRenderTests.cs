using PitGlow.Config;
using PitGlow.Devices;
using PitGlow.Engine;
using PitGlow.OpenRgb;
using PitGlow.Rendering;

namespace PitGlow.Tests;

public class ConfigTests
{
    internal static IReadOnlyList<DeviceInfo> RigInfos() =>
        new OpenRgbBackend().Describe([.. FakeOpenRgbServer.UserRig().Select(c => c.Data)]);

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void SuggestedConfigForUsersRig()
    {
        var infos = RigInfos();
        var cfg = Suggest.Config(infos);
        var expected = new (string, int, string?)[]
        {
            ("Corsair Commander Core", 0, null), ("MSI GeForce RTX 5090 VANGUARD SOC", 0, "flags"),
            ("Kingston Fury DDR5 DRAM", 0, "flags"), ("Razer Naga Classic", 0, "flags"),
        };
        Assert.Equal(expected, cfg.Devices.Select(d => (d.Name, d.Index, d.Role)).ToArray());
        var core = cfg.Devices[0];
        Assert.Equal(20, core.MaxFps);
        Assert.Contains("vid_1b1c", core.Location);
        var roles = core.Zones.ToDictionary(z => z.Zone, z => z.Role);
        Assert.Equal("flags", roles["Pump"]);
        Assert.All(Enumerable.Range(1, 6), i => Assert.Equal("tach", roles[$"Corsair Fan Header {i}"]));
        var plan = Layouts.Build(cfg, infos);
        Assert.Empty(plan.Problems);
        var groups = plan.Layouts.SelectMany(l => l.Groups).ToList();
        Assert.Equal(6, groups.Count(g => g.Role == "tach" && g.Leds.Count == 8));
        Assert.Equal(4, groups.Count(g => g.Role == "flags"));   // pump, GPU, RAM, mouse
    }

    [Fact]
    public void TomlRoundTripWithEdits()
    {
        var cfg = Suggest.Config(RigInfos());
        cfg.Devices[0].Zones[1].StartLed = 5;
        cfg.Devices[0].Zones[1].Reverse = true;
        cfg.WhenClear = "off";
        var text = SettingsFile.ToToml(cfg).Replace("[flags.style.YELLOW]\npattern = \"flash\"", "[flags.style.YELLOW]\npattern = \"pulse\"");
        var loaded = SettingsFile.Parse(text);
        Assert.Equal(cfg.Devices.Count, loaded.Devices.Count);
        for (int i = 0; i < cfg.Devices.Count; i++) Assert.True(cfg.Devices[i].SameAs(loaded.Devices[i]), cfg.Devices[i].Name);
        Assert.True(cfg.Tach.SameAs(loaded.Tach));
        Assert.Equal(cfg.Priority, loaded.Priority);
        Assert.Equal("off", loaded.WhenClear);
        Assert.Equal(FlagPattern.Pulse, loaded.FlagStyles[Flag.Yellow].Pattern);
        Assert.True(cfg.FlagStyles[Flag.Green].SameAs(loaded.FlagStyles[Flag.Green]));
    }

    [Fact]
    public void BadConfigIsRejected()
    {
        Assert.Throws<ConfigException>(() => SettingsFile.Parse("[[device]]\nname = \"x\"\nrole = \"rpm\"\n"));
        Assert.Throws<ConfigException>(() => SettingsFile.Parse("[tach]\nshift_color = \"red\"\n"));
        Assert.Throws<ConfigException>(() => SettingsFile.Parse("[tach\n"));
        Assert.Throws<ConfigException>(() => SettingsFile.Parse("[flags]\npriority = [\"PURPLE\"]\n"));
    }

    [Fact]
    public void ReadsTheUsersRealSettingsFile()
    {
        var cfg = SettingsFile.Parse(Fixture("user-openrgb11.toml"));
        var core = cfg.Devices.Single(d => d.Name == "Corsair Commander Core");
        Assert.Equal(4, core.Zones[1].StartLed);
        Assert.False(core.Zones[1].Reverse);
        Assert.All(core.Zones.Skip(2), z => Assert.Equal((6, true), (z.StartLed, z.Reverse)));
        var plan = Layouts.Build(cfg, RigInfos());
        Assert.Empty(plan.Problems);
        Assert.Equal(4, plan.Layouts.Count);                                         // GPU, mouse, RAM, core
        Assert.True(plan.SettingsChanged);                                           // learns locations
        Assert.All(cfg.Devices, d => Assert.False(string.IsNullOrEmpty(d.Location)));
    }

    [Fact]
    public void OldOpenRgb09NamesAreFoundAgainAfterTheUpdate()
    {
        var cfg = SettingsFile.Parse(Fixture("user-openrgb09.toml"));
        var plan = Layouts.Build(cfg, RigInfos());
        Assert.Empty(plan.Problems);
        Assert.True(plan.SettingsChanged);
        Assert.Contains(cfg.Devices, d => d.Name == "Kingston Fury DDR5 DRAM");
        var core = cfg.Devices.Single(d => d.Name == "Corsair Commander Core");
        Assert.Equal(Enumerable.Range(1, 6).Select(i => $"Corsair Fan Header {i}"), core.Zones.Skip(1).Select(z => z.Zone));
        Assert.Equal(4, core.Zones[1].StartLed);   // calibration kept through the rename
        var tach = plan.Layouts.SelectMany(l => l.Groups).Count(g => g.Role == "tach");
        Assert.Equal(6, tach);
        // Saved and reloaded, the next start needs no guessing.
        var again = Layouts.Build(SettingsFile.Parse(SettingsFile.ToToml(cfg)), RigInfos());
        Assert.False(again.SettingsChanged);
        Assert.Empty(again.Notes);
    }

    [Fact]
    public void ZoneRenameFoundByIndex()
    {
        var cfg = Suggest.Config(RigInfos());
        cfg.Devices[0].Zones[3].Zone = "Something Old";
        var plan = Layouts.Build(cfg, RigInfos());
        Assert.Empty(plan.Problems);
        Assert.Equal("Corsair Fan Header 3", cfg.Devices[0].Zones[3].Zone);
    }

    [Fact]
    public void EmptyZoneIsReportedNotFatal()
    {
        var rig = FakeOpenRgbServer.UserRig();
        rig[1] = new FakeController("Corsair Commander Core", 3,
            [("Pump", 29), .. Enumerable.Range(1, 6).Select(i => ($"Corsair Fan Header {i}", 0))], location: rig[1].Data.Location);
        var infos = new OpenRgbBackend().Describe([.. rig.Select(c => c.Data)]);
        var plan = Layouts.Build(Suggest.Config(infos), infos);
        Assert.Contains(plan.Problems, p => p.Contains("0 LEDs"));
        Assert.Contains(plan.Layouts, l => l.Info.Name == "Corsair Commander Core");   // pump still works
    }
}

public class RenderTests
{
    private readonly IReadOnlyList<DeviceInfo> _infos = ConfigTests.RigInfos();
    private readonly Settings _cfg;
    private readonly LayoutPlan _plan;

    public RenderTests()
    {
        _cfg = Suggest.Config(_infos);
        _plan = Layouts.Build(_cfg, _infos);
    }

    private Layout For(string name) => _plan.Layouts.Single(l => l.Info.Name == name);

    [Fact]
    public void FansShowRpmAndPumpShowsFlagInOneFrame()
    {
        var r = new Renderer(_cfg);
        var core = For("Corsair Commander Core");
        var s = new Sample("t") { Rpm = 8100, FirstRpm = 4000, ShiftRpm = 8000, MaxRpm = 8500, Flags = new HashSet<Flag> { Flag.Red } };
        r.Update(s, 0.0);
        var frame = r.Frame(core, s, 0.0);
        Assert.Equal(29 + 48, frame.Length);
        var pump = core.Info.Zone("Pump")!.Leds;
        var port1 = core.Info.Zone("Corsair Fan Header 1")!.Leds;
        Assert.All(port1, i => Assert.Equal(new Rgb(255, 0, 0), frame[i]));   // at shift: all red
        Assert.All(pump, i => Assert.Equal(new Rgb(255, 0, 0), frame[i]));    // red flag: solid red
        var s2 = new Sample("t") { Rpm = 1000, FirstRpm = 4000, ShiftRpm = 8000, Flags = new HashSet<Flag> { Flag.Blue } };
        r.Update(s2, 1.5);                                                    // red has cleared (after linger)
        frame = r.Frame(core, s2, 1.5);
        Assert.All(port1, i => Assert.Equal(Rgb.Off, frame[i]));              // low revs: gauge empty
        Assert.All(pump, i => Assert.True(frame[i].B > frame[i].R));          // blue on the pump
    }

    [Fact]
    public void NoFlagShowsOriginalColors()
    {
        var r = new Renderer(_cfg);
        var s = new Sample("t") { Rpm = 1000 };
        r.Update(s, 0);
        var gpu = For("MSI GeForce RTX 5090 VANGUARD SOC");
        Assert.Equal(gpu.Info.OriginalColors, r.Frame(gpu, s, 0));
    }

    [Fact]
    public void SingleLedGpuStillShowsFlags()
    {
        var r = new Renderer(_cfg);
        var s = new Sample("t") { Flags = new HashSet<Flag> { Flag.Yellow } };
        r.Update(s, 0);
        Assert.Equal(new[] { new Rgb(255, 170, 0) }, r.Frame(For("MSI GeForce RTX 5090 VANGUARD SOC"), s, 0));
    }

    [Fact]
    public void LearnsMaxRpmWhenGameHasNone()
    {
        var r = new Renderer(_cfg);
        foreach (var rpm in new[] { 3000, 6800, 5000 }) r.Update(new Sample("beamng") { Rpm = rpm }, 0);
        Assert.Equal(6800, r.LearnedMax);
    }
}
