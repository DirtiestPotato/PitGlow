using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using PitGlow.Sources;

namespace PitGlow.Tests;

public class IRacingTests
{
    [Fact]
    public void FlagBits()
    {
        AssertFlags.Equal(IRacingSource.FlagsFromBits(0x4), Flag.Green);
        AssertFlags.Equal(IRacingSource.FlagsFromBits(0x80000000), Flag.Green);
        Assert.True(IRacingSource.FlagsFromBits(0x8 | 0x20).SetEquals([Flag.Yellow, Flag.Blue]));
        AssertFlags.Equal(IRacingSource.FlagsFromBits(0x4000), Flag.FullCourseYellow);
        AssertFlags.Equal(IRacingSource.FlagsFromBits(0x100000), Flag.Meatball);
        AssertFlags.Equal(IRacingSource.FlagsFromBits(0x80000), Flag.BlackWhite);
        // Observed live: black flag plus "start hidden", "servicible" and "one lap to green".
        AssertFlags.Equal(IRacingSource.FlagsFromBits(0x10050200), Flag.Black);
        Assert.Empty(IRacingSource.FlagsFromBits(0x200 | 0x40000));
    }

    private sealed class FakeSdk : IIRacingSdk
    {
        public IRacingSnapshot Snap = new(1.0, true, false, 6500.0, 0x8, 0x10);
        public string Info = """
            DriverInfo:
             DriverCarIdx: 0
             DriverCarRedLine: 7500.000
             DriverCarSLFirstRPM: 5000.000
             DriverCarSLShiftRPM: 7200.000
             DriverCarSLLastRPM: 7300.000
             DriverCarSLBlinkRPM: 7400.000
            """;
        public bool IsConnected { get; private set; }
        public int Startups, Shutdowns;
        public bool Startup() { Startups++; return IsConnected = true; }
        public void Shutdown() { Shutdowns++; IsConnected = false; }
        public IRacingSnapshot? Read() => Snap;
        public string? SessionInfo() => Info;
    }

    [Fact]
    public void NextRaceIsPickedUpAfterLeavingOne()
    {
        // Race 1 runs, you leave (telemetry freezes while the sim closes), race 2 starts fresh.
        var fake = new FakeSdk();
        var src = new IRacingSource(fake);
        Assert.NotNull(src.Read(0.0));
        Assert.Equal(1, fake.Startups);
        for (double t = 0.5; t <= 6.0; t += 0.5) src.Read(t);         // frozen: nothing to show
        Assert.Equal(1, fake.Shutdowns);                               // let go of the old session
        fake.Snap = fake.Snap with { SessionTime = 0.1, Rpm = 3000 };  // new sim, new session clock
        fake.Info = fake.Info.Replace("7200.000", "8100.000");         // and maybe a different car
        Sample? s = null;
        for (double t = 6.5; t <= 8.0 && s is null; t += 0.5) { s = src.Read(t); fake.Snap = fake.Snap with { SessionTime = fake.Snap.SessionTime + 0.5 }; }
        Assert.NotNull(s);
        Assert.Equal(2, fake.Startups);                                // reconnected from scratch
        Assert.Equal(8100.0, s!.ShiftRpm);                             // new car's lights read again
    }

    [Fact]
    public void ReadsCarShiftLights()
    {
        var fake = new FakeSdk();
        var src = new IRacingSource(fake);
        var s = src.Read(0.0)!;
        Assert.Equal((6500.0, 5000.0, 7200.0, 7400.0, 7500.0), (s.Rpm, s.FirstRpm!.Value, s.ShiftRpm!.Value, s.BlinkRpm!.Value, s.MaxRpm!.Value));
        Assert.True(s.PitLimiter);
        AssertFlags.Equal(s.Flags, Flag.Yellow);
        fake.Snap = fake.Snap with { IsOnTrack = false };
        Assert.Null(src.Read(0.1));
        fake.Snap = fake.Snap with { IsOnTrack = true, IsReplayPlaying = true };
        Assert.Null(src.Read(0.2));
        fake.Snap = fake.Snap with { IsReplayPlaying = false };
        Assert.Null(src.Read(3.0));   // SessionTime frozen > 2 s: sim paused or gone
    }

    [Fact]
    public void ParsesLightsFromSessionYaml()
    {
        var lights = IRacingSource.ParseLights("  DriverCarSLShiftRPM: 7200.000\n  DriverCarRedLine: 0.000\n");
        Assert.Equal(7200.0, lights["DriverCarSLShiftRPM"]);
        Assert.Null(lights["DriverCarRedLine"]);
    }
}

public class KunosTests
{
    private static void Put(byte[] buf, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), value);

    private static Func<string, IMemoryBlock> Opener(Dictionary<string, byte[]> blocks) =>
        name => new BytesBlock(blocks.GetValueOrDefault(name));

    private static (ACEvoSource, byte[], byte[]) Make()
    {
        var physics = new byte[800];
        var graphics = new byte[4900];
        Put(physics, 20, 7123);
        Put(physics, 248, 1);
        Put(physics, 588, 8500);
        Put(graphics, 4, 2);       // AC_LIVE
        Put(graphics, 2404, 4);    // driver flag: blue
        Put(graphics, 2408, 5);    // global flag: yellow -> full course
        var blocks = new Dictionary<string, byte[]>
        {
            [@"Local\acevo_pmf_physics"] = physics, [@"Local\acevo_pmf_graphics"] = graphics, [@"Local\acevo_pmf_static"] = new byte[256],
        };
        return (new ACEvoSource(Opener(blocks)), physics, graphics);
    }

    [Fact]
    public void ReadsLiveData()
    {
        var (src, physics, graphics) = Make();
        Assert.Null(src.Read(0.0));   // must see the packet counter move first
        Put(physics, 0, 1);
        var s = src.Read(0.1)!;
        Assert.Equal((7123.0, 8500.0, true), (s.Rpm, s.MaxRpm!.Value, s.PitLimiter));
        Assert.True(s.Flags.SetEquals([Flag.Blue, Flag.FullCourseYellow]));
        graphics[43] = 1;
        Put(physics, 0, 2);
        Assert.True(src.Read(0.2)!.ShiftNow);
        Put(graphics, 4, 3);       // paused
        Put(physics, 0, 3);
        Assert.Null(src.Read(0.3));
    }

    [Fact]
    public void StaleGameIsReleased()
    {
        var (src, physics, _) = Make();
        src.Read(0.0);
        Put(physics, 0, 1);
        Assert.NotNull(src.Read(0.1));
        Assert.NotNull(src.Read(1.0));
        Assert.Null(src.Read(2.0));
        Assert.False(src.IsOpen);
    }

    [Fact]
    public void LeftoverBlockThatNeverMovesIsNeverAGame()
    {
        // A closed game's block kept alive by another app: same bytes forever.
        var physics = new byte[800];
        Put(physics, 0, 41);
        var src = new ACClassicSource(Opener(new() { [@"Local\acpmf_physics"] = physics }));
        for (double t = 0; t < 10; t += 0.1)
            Assert.Null(src.Read(t));
        // When the game really starts, the page moves and it is picked up straight away.
        Put(physics, 0, 42);
        Put(physics, 20, 2500);
        Assert.NotNull(src.Read(10.05));
    }

    [Fact]
    public void GameNotRunning() => Assert.Null(new ACEvoSource(Opener(new())).Read(0.0));

    [Fact]
    public void RallyUsesPhysicsAndStaticOnly()
    {
        var physics = new byte[800];
        var stat = new byte[800];
        Put(physics, 20, 6400);
        Put(stat, 412, 7800);
        var src = new ACClassicSource(Opener(new() { [@"Local\acpmf_physics"] = physics, [@"Local\acpmf_static"] = stat }));
        src.Read(0.0);
        Put(physics, 0, 9);
        var s = src.Read(0.05)!;
        Assert.Equal((6400.0, 7800.0), (s.Rpm, s.MaxRpm!.Value));
        Assert.Empty(s.Flags);
        Assert.False(s.HasFlags);
    }

    [Fact]
    public void RallyWithBlankGraphicsAndStuckPacketCounterStillWorks()
    {
        var physics = new byte[800];
        var graphics = new byte[1588];     // AC Rally creates it but leaves status 0
        var stat = new byte[784];          // and no maxRpm
        Put(physics, 20, 3000);
        BinaryPrimitives.WriteSingleLittleEndian(physics.AsSpan(588), 7200f);   // currentMaxRpm as float
        var src = new ACClassicSource(Opener(new()
        {
            [@"Local\acpmf_physics"] = physics, [@"Local\acpmf_graphics"] = graphics, [@"Local\acpmf_static"] = stat,
        }));
        Assert.Null(src.Read(0.0));
        Put(physics, 20, 3200);            // packetId stays 0; the revs move
        var s = src.Read(0.05)!;
        Assert.Equal((3200.0, 7200.0), (s.Rpm, s.MaxRpm!.Value));
        Put(physics, 20, 3300);
        Assert.NotNull(src.Read(1.0));
        Put(graphics, 4, 3);               // an explicit pause still turns the lights off
        Put(physics, 20, 3400);
        Assert.Null(src.Read(1.1));
        Put(graphics, 4, 0);
        Assert.NotNull(src.Read(1.2));
        Assert.Null(src.Read(3.0));        // frozen for > 1.5 s: game paused or closed
        Put(physics, 588, 7600);           // ACC stores currentMaxRpm as an int
        Assert.Equal(7600.0, ACClassicSource.CurrentMaxRpm(new BytesBlock(physics)));
    }
}

public class UdpTests
{
    public static byte[] Wf2Packet(int rpm = 6000, int rpmMax = 7500, int redline = 7000, int marshal = 0, byte status = 0x20)
    {
        var buf = new byte[1218];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, Wreckfest2Source.Signature);
        buf[4] = 0;
        buf[5] = status;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(14), (ushort)marshal);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(435), rpm);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(439), rpmMax);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(443), redline);
        return buf;
    }

    private static byte[] OutgaugePacket(float rpm = 5000f, uint showLights = 0)
    {
        var buf = new byte[96];
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(16), rpm);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(44), showLights);
        return buf;
    }

    [Fact]
    public void Wreckfest2Parse()
    {
        var src = new Wreckfest2Source(0);
        var s = src.Parse(Wf2Packet(marshal: (1 << 6) | (1 << 2)))!;
        Assert.Equal((6000.0, 7000.0, 7500.0), (s.Rpm, s.ShiftRpm!.Value, s.MaxRpm!.Value));
        Assert.True(s.Flags.SetEquals([Flag.Blue, Flag.Checkered]));
        Assert.Null(src.Parse(new byte[1218]));                 // wrong signature
        var other = Wf2Packet();
        other[4] = 4;                                          // not the Main packet
        Assert.Null(src.Parse(other));
    }

    [Fact]
    public void SilentWreckfestIsReported()
    {
        int port;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        bool running = false;
        var src = new Wreckfest2Source(port, gameRunning: () => running);
        src.Read(0);
        Assert.Null(src.Error);                                        // game not running: nothing to say
        running = true;
        src.Read(2);
        src.Read(25);
        Assert.Equal(Wreckfest2Source.SilentWarning, src.Error);       // running 20+ s, no packets
        using var tx = new UdpClient();
        tx.Send(Wf2Packet(), new IPEndPoint(IPAddress.Loopback, port));
        Thread.Sleep(100);
        Assert.NotNull(src.Read(28));
        Assert.Null(src.Error);                                        // telemetry arrived: warning cleared
        running = false;
        src.Read(31);
        Assert.Null(src.Error);
        src.Close();
    }

    [Fact]
    public void BeamNGParse()
    {
        var s = new BeamNGSource(0).Parse(OutgaugePacket(4321f, 1 | 8))!;
        Assert.Equal(4321.0, s.Rpm);
        Assert.True(s.ShiftNow && s.PitLimiter);
    }

    [Fact]
    public void LiveUdpAndPause()
    {
        int port;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        var src = new Wreckfest2Source(port);
        Assert.Null(src.Read(0.0));
        using var tx = new UdpClient();
        var to = new IPEndPoint(IPAddress.Loopback, port);
        tx.Send(Wf2Packet(rpm: 5555), to);
        Thread.Sleep(100);
        Assert.Equal(5555.0, src.Read(0.1)!.Rpm);
        tx.Send(Wf2Packet(status: 0x1), to);                  // paused
        Thread.Sleep(100);
        Assert.Null(src.Read(0.2));
        tx.Send(Wf2Packet(rpm: 4000), to);
        Thread.Sleep(100);
        Assert.Equal(4000.0, src.Read(0.3)!.Rpm);
        Assert.Null(src.Read(1.5));                            // packets stopped
        src.Close();
    }
}

public class AutoTests
{
    private sealed class Fake(string game) : ITelemetrySource
    {
        public bool On;
        public string Game => game;
        public Sample? Read(double now) => On ? new DemoSource().Read(now) : null;
        public void Close() { }
    }

    [Fact]
    public void FollowsWhicheverGameIsDriving()
    {
        var a = new Fake("a");
        var b = new Fake("b");
        var auto = new AutoSource([a, b]);
        Assert.Null(auto.Read(0));
        b.On = true;
        Assert.NotNull(auto.Read(1));
        Assert.Same(b, auto.Active);
        b.On = false;
        a.On = true;
        Assert.Null(auto.Read(1.1));    // b stopped; rescan waits 0.5 s
        Assert.NotNull(auto.Read(1.6));
        Assert.Same(a, auto.Active);
    }

    [Fact]
    public void DemoCyclesFlagsAndLimiter()
    {
        var demo = new DemoSource(flagSeconds: 1, flags: [Flag.Yellow, null]);
        AssertFlags.Equal(demo.Read(0).Flags, Flag.Yellow);
        Assert.Empty(demo.Read(1.5).Flags);
        Assert.True(demo.Read(13).PitLimiter);
        Assert.False(demo.Read(16).PitLimiter);
    }
}

public class Wreckfest2ConfigTests
{
    // Exactly what Sim Racing Studio writes when it starts.
    private const string SrsConfig = """
        {
            "udp": [
                {
                    "enabled": 1,
                    "ip": "127.0.0.1",
                    "port": "23123"
                }
            ],
            "logging": [ { "format": "none" } ],
            "server": [ { "enabled": 0, "automaticMainCar": 0 } ]
        }
        """;

    private static System.Text.Json.Nodes.JsonArray Udp(string json) =>
        (System.Text.Json.Nodes.JsonArray)System.Text.Json.Nodes.JsonNode.Parse(json)!["udp"]!;

    [Fact]
    public void AddsPitGlowNextToTheMotionRig()
    {
        var fixedText = Wreckfest2Config.Ensure(SrsConfig, 23124)!;
        var udp = Udp(fixedText);
        Assert.Equal(2, udp.Count);
        Assert.Equal("23123", udp[0]!["port"]!.ToString());                 // rig untouched
        Assert.Equal(("23124", "127.0.0.1", "1"), (udp[1]!["port"]!.ToString(), udp[1]!["ip"]!.ToString(), udp[1]!["enabled"]!.ToString()));
        Assert.Contains("automaticMainCar", fixedText);                       // rest of the file kept
        Assert.Null(Wreckfest2Config.Ensure(fixedText, 23124));               // already there: no rewrite
    }

    [Fact]
    public void ReEnablesADisabledEntryAndHandlesOddFiles()
    {
        var disabled = SrsConfig.Replace("\"port\": \"23123\"", "\"port\": 23124").Replace("\"enabled\": 1,\n            \"ip\"", "\"enabled\": 0,\n            \"ip\"");
        var udp = Udp(Wreckfest2Config.Ensure(disabled.Replace("\"enabled\": 1", "\"enabled\": 0"), 23124)!);
        Assert.Single(udp);
        Assert.Equal("1", udp[0]!["enabled"]!.ToString());
        Assert.Single(Udp(Wreckfest2Config.Ensure("{}", 23124)!));
        Assert.Null(Wreckfest2Config.Ensure(SrsConfig, 23123));               // old setups on 23123 are left alone
    }

    [Fact]
    public void KeeperRestoresTheEntryWithinMillisecondsOfARewrite()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pitglow-wf2w-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "savegame", "telemetry", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, SrsConfig);
            var keeper = new Wreckfest2Config(23124, watch: () => [path]);
            keeper.Check(0);                                                   // fixes it and starts watching
            Assert.Equal(2, Udp(File.ReadAllText(path)).Count);
            keeper.Check(30);                                                  // well past the anti-fight window
            File.WriteAllText(path, SrsConfig);                                // motion software, as the game starts
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (Udp(File.ReadAllText(path)).Count < 2 && sw.ElapsedMilliseconds < 2000) Thread.Sleep(10);
            Assert.Equal(2, Udp(File.ReadAllText(path)).Count);
            Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");   // no waiting for the next poll
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void KeeperPutsTheEntryBackAfterARewrite()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pitglow-wf2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        try
        {
            File.WriteAllText(path, SrsConfig);
            var keeper = new Wreckfest2Config(23124, () => [path]);
            keeper.Check(0);
            Assert.Equal(2, Udp(File.ReadAllText(path)).Count);
            File.WriteAllText(path, SrsConfig);                                // motion software starts again
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
            keeper.Check(1);                                                   // too soon after our own write
            Assert.Single(Udp(File.ReadAllText(path)));
            keeper.Check(11);
            Assert.Equal(2, Udp(File.ReadAllText(path)).Count);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
