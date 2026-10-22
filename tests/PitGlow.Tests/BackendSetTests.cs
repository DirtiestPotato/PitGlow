using PitGlow.Backends;
using PitGlow.Config;
using PitGlow.Devices;
using PitGlow.Engine;
using PitGlow.OpenRgb;
using PitGlow.Rendering;

namespace PitGlow.Tests;

/// <summary>An in-memory lighting route, standing in for iCUE or Synapse.</summary>
internal sealed class FakeBackend(string id, IReadOnlyList<DeviceInfo>? devices, string? error = null) : IBackend
{
    public readonly List<(string Device, Rgb[] Frame)> Writes = [];
    public int Acquired, Released;

    public string Id => id;
    public string DisplayName => $"Fake {id}";
    public BackendStatus Probe(TimeSpan timeout) => new(error is null, error ?? "ok");

    public IReadOnlyList<DeviceInfo> Discover(TimeSpan timeout) =>
        error is null ? devices ?? [] : throw new BackendException(error);

    public IDeviceSession OpenSession(DeviceInfo device) => new Session(this, device);

    private sealed class Session(FakeBackend b, DeviceInfo info) : IDeviceSession
    {
        public void Connect() { }
        public VerifyResult Verify(bool engaged) => VerifyResult.Ok;
        public void Acquire() { lock (b.Writes) b.Acquired++; }
        public void Write(IReadOnlyList<Rgb> frame) { lock (b.Writes) b.Writes.Add((info.Label, frame.ToArray())); }
        public void Release() { lock (b.Writes) b.Released++; }
        public void RestoreTo(string? modeName, IReadOnlyList<Rgb> colors) => Release();
        public void Dispose() { }
    }

    /// <summary>Your Commander Core as iCUE reports it: six fans and a pump, each a device.</summary>
    public static IReadOnlyList<DeviceInfo> CorsairViaICue() =>
    [
        .. Enumerable.Range(0, 6).Select(i => Device("icue", "Corsair Commander Core QL Fan", i, DeviceKind.Fan, 8, $"icue:{{core}}:Fan{i + 1}")),
        Device("icue", "Corsair Commander Core Pump", 0, DeviceKind.Cooler, 29, "icue:{core}:Pump"),
    ];

    public static DeviceInfo Device(string backend, string name, int occurrence, DeviceKind kind, int leds, string location) => new()
    {
        Backend = backend, Name = name, Occurrence = occurrence, Kind = kind, Location = location, LedCount = leds,
        Zones = [new LedZone("LEDs", 0, Enumerable.Range(0, leds).ToArray())],
        OriginalColors = [.. Enumerable.Repeat(new Rgb(1, 2, 3), leds)],
    };
}

public class BackendSetTests
{
    private static IReadOnlyList<DeviceInfo> OpenRgbRig() => ConfigTests.RigInfos();

    [Fact]
    public void VendorRouteTakesItsDevicesAwayFromOpenRgb()
    {
        var set = new BackendSet([new FakeBackend("icue", FakeBackend.CorsairViaICue()), new FakeBackend("openrgb", OpenRgbRig())]);
        var result = set.Discover(TimeSpan.FromSeconds(1));
        Assert.DoesNotContain(result.Devices, d => d.Backend == "openrgb" && d.Name == "Corsair Commander Core");
        Assert.Equal(7, result.Devices.Count(d => d.Backend == "icue"));
        Assert.Contains(result.Devices, d => d.Name == "Kingston Fury DDR5 DRAM" && d.Backend == "openrgb");
        Assert.Contains(result.Notes, n => n.Contains("left to the vendor"));
    }

    [Fact]
    public void WithoutICueOpenRgbKeepsTheCorsairGear()
    {
        var set = new BackendSet([new FakeBackend("icue", null, "iCUE is not running"), new FakeBackend("openrgb", OpenRgbRig())]);
        var result = set.Discover(TimeSpan.FromSeconds(1));
        Assert.Contains(result.Devices, d => d.Name == "Corsair Commander Core" && d.Backend == "openrgb");
        Assert.Equal("icue", Assert.Single(result.Failed));
        Assert.Contains(result.Notes, n => n.Contains("iCUE is not running"));
    }

    [Fact]
    public void OpenRgbIsOptional()
    {
        var set = new BackendSet([new FakeBackend("icue", FakeBackend.CorsairViaICue()), new FakeBackend("openrgb", null, "Can't reach OpenRGB")]);
        var result = set.Discover(TimeSpan.FromSeconds(1));
        Assert.Equal(7, result.Devices.Count);
        var cfg = Suggest.Config(result.Devices);
        Assert.Equal(6, cfg.Devices.Count(d => d.Backend == "icue" && d.Zones.Single().Role == Roles.Tach));
        Assert.Single(cfg.Devices, d => d.Backend == "icue" && d.Role == Roles.Flags);   // the pump
        var plan = Layouts.Build(cfg, result.Devices);
        Assert.Empty(plan.Problems);
        Assert.Equal(7, plan.Layouts.Count);
    }

    [Fact]
    public void RunnerDrivesDevicesOnSeveralRoutesAtOnce()
    {
        var icue = new FakeBackend("icue", FakeBackend.CorsairViaICue());
        using var server = new FakeOpenRgbServer(FakeOpenRgbServer.UserRig());
        var set = new BackendSet([icue, new OpenRgbBackend("127.0.0.1", server.Port)]);
        var devices = set.Discover(TimeSpan.FromSeconds(5)).Devices;
        var cfg = Suggest.Config(devices);
        var plan = Layouts.Build(cfg, devices);
        new Runner(cfg, new PitGlow.Sources.DemoSource(flagSeconds: 0.3, flags: [Flag.Yellow]), set.OpenSession, plan.Layouts, null)
            .Run(CancellationToken.None, seconds: 1.0);
        lock (icue.Writes)
        {
            Assert.Contains(icue.Writes, w => w.Device.Contains("Fan"));
            Assert.Contains(icue.Writes, w => w.Device.Contains("Pump") && w.Frame[0] == new Rgb(255, 170, 0));
            Assert.Equal(icue.Acquired, icue.Released);   // everything handed back
        }
        Assert.Equal(0, server.Devices[1].WriteCount);    // OpenRGB never touched the Commander Core
        Assert.True(server.Devices[3].WriteCount > 0);   // ... but still drove the RAM
    }

    [Fact]
    public void BackendIsSavedPerDevice()
    {
        var cfg = Suggest.Config(FakeBackend.CorsairViaICue());
        cfg.Devices[2].Zones[0].StartLed = 3;
        var loaded = SettingsFile.Parse(SettingsFile.ToToml(cfg));
        Assert.All(loaded.Devices, d => Assert.Equal("icue", d.Backend));
        Assert.Equal(3, loaded.Devices[2].Zones[0].StartLed);
        Assert.True(loaded.ICueEnabled && loaded.OpenRgbEnabled && loaded.RazerEnabled);
        var off = SettingsFile.Parse("[icue]\nenabled = false\n[openrgb]\nenabled = false\n");
        Assert.False(off.ICueEnabled);
        Assert.False(off.OpenRgbEnabled);
    }

    [Fact]
    public void SameNameOnDifferentRoutesNeverMixes()
    {
        var cfg = new Settings();
        cfg.Devices.Add(new DeviceCfg { Name = "Corsair Commander Core Pump", Backend = "openrgb", Role = Roles.Flags });
        var plan = Layouts.Build(cfg, FakeBackend.CorsairViaICue());
        Assert.Empty(plan.Layouts);
        Assert.Contains(plan.Problems, p => p.Contains("openrgb"));
    }
}
