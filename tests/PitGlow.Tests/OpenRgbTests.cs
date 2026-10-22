using PitGlow.Backends;
using PitGlow.Config;
using PitGlow.Engine;
using PitGlow.OpenRgb;
using PitGlow.Rendering;
using PitGlow.Sources;

namespace PitGlow.Tests;

public class ProtocolTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(3u)]
    [InlineData(4u)]
    public void ControllerDataRoundTrips(uint protocol)
    {
        var data = FakeOpenRgbServer.UserRig()[2].Data with
        {
            Zones = [new ZoneData("Matrix", 2, 0, 4, 4, 2, 2, [0, 1, 2, 3])],
            Leds = [.. Enumerable.Range(0, 4).Select(i => new LedData($"L{i}", (uint)i))],
            Colors = [1, 2, 3, 4],
        };
        var back = ControllerData.Parse(data.Serialize(protocol), protocol);
        Assert.Equal(data.Name, back.Name);
        Assert.Equal(protocol >= 1 ? data.Vendor : "", back.Vendor);
        Assert.Equal(data.Location, back.Location);
        Assert.Equal(data.Modes.Count, back.Modes.Count);
        Assert.Equal(data.Modes[1].ColorMode, back.Modes[1].ColorMode);
        Assert.Equal(data.Zones[0].Matrix, back.Zones[0].Matrix);
        Assert.Equal(data.Colors, back.Colors);
    }

    [Fact]
    public void ColorsPackLikeOpenRgb()
    {
        Assert.Equal(0x00332211u, ControllerData.Pack(new Rgb(0x11, 0x22, 0x33)));
        Assert.Equal(new Rgb(0x11, 0x22, 0x33), ControllerData.Unpack(0x00332211u));
    }

    [Fact]
    public void UpdateModeAndLedPayloadsRoundTrip()
    {
        var mode = new ModeData { Name = "Static", Value = 7, ColorMode = ColorMode.ModeSpecific, Colors = [0xFF] };
        var (index, back) = ControllerData.ParseUpdateMode(ControllerData.UpdateModePayload(1, mode, 3), 3);
        Assert.Equal(1, index);
        Assert.Equal(mode.Name, back.Name);
        Assert.Equal(mode.Colors, back.Colors);
        Assert.Equal(new List<uint> { 5u, 6u }, ControllerData.ParseUpdateLeds(ControllerData.UpdateLedsPayload(new uint[] { 5u, 6u })));
    }
}

public sealed class OpenRgbBackendTests : IDisposable
{
    private readonly FakeOpenRgbServer _server = new(FakeOpenRgbServer.UserRig());
    private OpenRgbBackend Backend => new("127.0.0.1", _server.Port);

    public void Dispose() => _server.Dispose();

    [Theory]
    [InlineData(4u)]   // OpenRGB 0.9
    [InlineData(5u)]   // OpenRGB 1.x: still answered in protocol 3
    public void DiscoversTheRig(uint serverVersion)
    {
        _server.Version = serverVersion;
        var infos = Backend.Discover(TimeSpan.FromSeconds(5));
        Assert.Equal(5, infos.Count);
        var core = infos.Single(i => i.Name == "Corsair Commander Core");
        Assert.Equal(PitGlow.Devices.DeviceKind.Cooler, core.Kind);
        Assert.Equal(77, core.LedCount);
        Assert.Equal("1B1C:0C32", core.UsbId);
        Assert.Equal(Enumerable.Range(29, 8), core.Zone("Corsair Fan Header 1")!.Leds);
        Assert.Equal("Rainbow Wave", infos.Single(i => i.Kind == PitGlow.Devices.DeviceKind.Gpu).OriginalMode);
    }

    [Fact]
    public void ProbeReportsWhatItFound()
    {
        var status = Backend.Probe(TimeSpan.FromSeconds(5));
        Assert.True(status.Available, status.Detail);
        Assert.Contains("5 devices", status.Detail);
    }

    [Fact]
    public void HungServerTimesOutWithAPlainMessage()
    {
        _server.Hang = true;
        var ex = Assert.Throws<BackendException>(() => Backend.Discover(TimeSpan.FromSeconds(1)));
        Assert.Contains("not responding", ex.Message + " " + (ex.InnerException?.Message ?? ""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoServerSaysHowToFixIt()
    {
        var port = _server.Port;
        _server.Dispose();
        var ex = Assert.Throws<BackendException>(() => new OpenRgbBackend("127.0.0.1", port).Discover(TimeSpan.FromSeconds(5)));
        Assert.Contains("Start Server", ex.Message);
    }

    private DeviceWorker Worker(PitGlow.Devices.DeviceInfo info, double fps = 100, RestoreJournal? journal = null)
    {
        var backend = Backend;
        return new DeviceWorker(info, () => backend.OpenSession(info), fps, journal);
    }

    [Fact]
    public void EngagesDirectStreamsAndRestores()
    {
        var infos = Backend.Discover(TimeSpan.FromSeconds(5));
        var gpu = _server.Devices[2];
        var w = Worker(infos[2]);
        w.Start();
        w.Submit([new Rgb(1, 2, 3)]);
        Assert.True(Wait.For(() => gpu.LastWrite?.SequenceEqual([new Rgb(1, 2, 3)]) == true));
        Assert.Equal("Direct", gpu.ActiveModeName);
        w.Submit([new Rgb(9, 9, 9)]);
        Assert.True(Wait.For(() => gpu.LastWrite?.SequenceEqual([new Rgb(9, 9, 9)]) == true));
        w.Submit(null);                                       // player left the car
        Assert.True(Wait.For(() => gpu.ActiveModeName == "Rainbow Wave"));
        w.Submit([new Rgb(5, 5, 5)]);
        Assert.True(Wait.For(() => gpu.LastWrite?.SequenceEqual([new Rgb(5, 5, 5)]) == true));
        w.Stop();
        Assert.Equal("Rainbow Wave", gpu.ActiveModeName);
        Assert.False(w.IsAlive);
        Assert.Empty(gpu.Violations);
    }

    [Fact]
    public void RestoresDirectDeviceColors()
    {
        var infos = Backend.Discover(TimeSpan.FromSeconds(5));
        var core = _server.Devices[1];
        var original = core.Colors();
        var w = Worker(infos[1]);
        w.Start();
        w.Submit(Enumerable.Repeat(new Rgb(200, 0, 0), 77).ToArray());
        Assert.True(Wait.For(() => core.WriteCount > 0));
        w.Stop();
        Assert.Equal(original, core.Colors());
        Assert.Empty(core.Violations);
    }

    [Fact]
    public void RateLimit()
    {
        var infos = Backend.Discover(TimeSpan.FromSeconds(5));
        var ram = _server.Devices[3];
        var w = Worker(infos[3], fps: 10);
        w.Start();
        var end = DateTime.UtcNow.AddSeconds(0.5);
        int i = 0;
        while (DateTime.UtcNow < end)
        {
            w.Submit(Enumerable.Repeat(new Rgb((byte)(i++ % 256), 0, 0), 24).ToArray());
            Thread.Sleep(5);
        }
        w.Stop();
        Assert.InRange(ram.WriteCount, 1, 8);                  // ~10 fps cap (+ restore write)
    }

    [Fact]
    public void ReconnectsAfterOpenRgbRestart()
    {
        var infos = Backend.Discover(TimeSpan.FromSeconds(5));
        var gpu = _server.Devices[2];
        var saved = DeviceWorker.RetrySeconds;
        DeviceWorker.RetrySeconds = 0.05;
        try
        {
            _server.FailNextConnects = 1;
            var w = Worker(infos[2]);
            w.Start();
            w.Submit([new Rgb(7, 7, 7)]);
            Assert.True(Wait.For(() => gpu.LastWrite?.SequenceEqual([new Rgb(7, 7, 7)]) == true));
            w.Stop();
        }
        finally
        {
            DeviceWorker.RetrySeconds = saved;
        }
    }

    [Fact]
    public void BacksOffWhenAnotherAppTakesTheDevice()
    {
        var infos = Backend.Discover(TimeSpan.FromSeconds(5));
        var gpu = _server.Devices[2];
        var (savedCheck, savedKeep) = (DeviceWorker.RecheckSeconds, DeviceWorker.KeepaliveSeconds);
        DeviceWorker.RecheckSeconds = 0.1;
        DeviceWorker.KeepaliveSeconds = 0.05;
        try
        {
            var w = Worker(infos[2]);
            w.Start();
            w.Submit([new Rgb(1, 1, 1)]);
            Assert.True(Wait.For(() => gpu.WriteCount > 0));
            gpu.ExternalModeChange(0);                          // e.g. the user picked Rainbow Wave
            Assert.True(Wait.For(() => w.Error?.Contains("backed off") == true));
            int writes = gpu.WriteCount;
            Thread.Sleep(300);
            Assert.Equal(writes, gpu.WriteCount);              // no fighting
            Assert.Equal("Rainbow Wave", gpu.ActiveModeName);
            w.Stop();
        }
        finally
        {
            (DeviceWorker.RecheckSeconds, DeviceWorker.KeepaliveSeconds) = (savedCheck, savedKeep);
        }
    }

    [Fact]
    public void RestoreJournalRecoversAfterACrash()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pitglow-test-{Guid.NewGuid():N}.json");
        var journal = new RestoreJournal(path);
        var infos = Backend.Discover(TimeSpan.FromSeconds(5));
        var gpu = _server.Devices[2];
        journal.Add(infos[2]);                                  // PitGlow took the GPU ...
        using (var session = Backend.OpenSession(infos[2]))
        {
            session.Connect();
            session.Acquire();
            session.Write([new Rgb(255, 0, 0)]);                // ... and was killed mid-race
        }
        Assert.Equal("Direct", gpu.ActiveModeName);
        var after = Backend.Discover(TimeSpan.FromSeconds(5));
        Assert.Equal(1, journal.Recover(Backend, after));
        Assert.Equal("Rainbow Wave", gpu.ActiveModeName);
        Assert.Empty(journal.Entries());
        Assert.False(File.Exists(path));
    }
}

public sealed class EndToEndTests : IDisposable
{
    private readonly FakeOpenRgbServer _server = new(FakeOpenRgbServer.UserRig());

    public void Dispose() => _server.Dispose();

    [Fact]
    public void DemoRunDrivesEveryDeviceThenRestores()
    {
        var rig = _server.Devices;
        var originals = rig.Select(d => (Colors: d.Colors(), Mode: d.ActiveModeName)).ToList();
        var backend = new OpenRgbBackend("127.0.0.1", _server.Port);
        var infos = backend.Discover(TimeSpan.FromSeconds(5));
        var cfg = Suggest.Config(infos);
        var plan = Layouts.Build(cfg, infos);
        var journal = new RestoreJournal(Path.Combine(Path.GetTempPath(), $"pitglow-e2e-{Guid.NewGuid():N}.json"));
        var source = new DemoSource(flagSeconds: 0.3, flags: [Flag.Yellow, Flag.Blue, null]);
        new Runner(cfg, source, backend, plan.Layouts, journal).Run(CancellationToken.None, seconds: 2.5);

        var (motherboard, core, gpu, ram, mouse) = (rig[0], rig[1], rig[2], rig[3], rig[4]);
        Assert.Equal(0, motherboard.WriteCount);                 // not ours: untouched
        foreach (var dev in new[] { core, gpu, ram, mouse }) Assert.True(dev.WriteCount > 3, $"{dev.Data.Name}: {dev.WriteCount} writes, mode {dev.ActiveModeName}, violations: {string.Join(',', dev.Violations)}");
        Assert.Contains(gpu.WritesCopy(), w => w[0] == new Rgb(255, 170, 0));                 // yellow seen
        var fanFrames = core.WritesCopy().Select(w => string.Join(",", w.Skip(29).Take(8))).Distinct().Count();
        Assert.True(fanFrames > 3, "gauge moved");
        for (int i = 0; i < rig.Count; i++)
        {
            Assert.Equal(originals[i].Mode, rig[i].ActiveModeName);                          // original mode back
            if (originals[i].Mode == "Direct") Assert.Equal(originals[i].Colors, rig[i].Colors());
            Assert.Empty(rig[i].Violations);
        }
        Assert.Empty(journal.Entries());
    }

    [Fact]
    public void CliInitThenDemoAgainstTheFakeServer()
    {
        var dir = Directory.CreateTempSubdirectory("pitglow-cli");
        var config = Path.Combine(dir.FullName, "pitglow.toml");
        var port = _server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PitGlow.Cli.Program.IncludeVendorBackends = false;   // no iCUE / Synapse on the build machine
        var output = new StringWriter();
        var stdout = Console.Out;
        Console.SetOut(output);
        try
        {
            Assert.Equal(0, PitGlow.Cli.Program.Main(["init", "--config", config, "--port", port]));
            Assert.Equal(0, PitGlow.Cli.Program.Main(["demo", "--config", config, "--port", port, "--seconds", "1"]));
            Assert.Equal(0, PitGlow.Cli.Program.Main(["devices", "--config", config, "--port", port]));
        }
        finally
        {
            Console.SetOut(stdout);
        }
        var text = output.ToString();
        Assert.Contains("RPM gauge", text);
        Assert.Contains("Corsair Fan Header 1", text);
        Assert.Contains("restoring your lighting", text);
        Assert.True(_server.Devices[1].WriteCount > 0);
        var saved = SettingsFile.Load(config);
        Assert.Contains(saved.Devices, d => d.Location?.Contains("vid_1b1c") == true);
    }
}
