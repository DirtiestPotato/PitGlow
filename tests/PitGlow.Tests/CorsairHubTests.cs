using PitGlow.Backends;
using PitGlow.CorsairHub;

namespace PitGlow.Tests;

/// <summary>A Commander Core in memory: answers commands the way the real hub does (65-byte reports).</summary>
internal sealed class FakeHub : IHubTransport
{
    private readonly Queue<byte[]> _replies = new();
    private readonly object _gate = new();
    private byte _endpoint;
    private List<byte>? _incoming;
    private int _incomingLen;

    public int ReportLength => 65;
    public bool Awake { get; private set; }
    public int Wakes, Sleeps;
    public byte[]? LastColors;
    public int Frames;
    public int PumpRpm = 2325;
    public double Coolant = 33.7;
    public int[] LedCounts = [29, 8, 8, 8, 8, 8, 8];
    public bool Disposed;

    public void Write(byte[] r)
    {
        lock (_gate)
        {
            Assert.Equal(65, r.Length);
            Assert.Equal(0x08, r[1]);
            var reply = new byte[65];
            reply[2] = r[2];
            switch (r[2])
            {
                case 0x01 when r[5] == 0x02: Awake = true; Wakes++; break;
                case 0x01: Awake = false; Sleeps++; break;
                case 0x02: reply[4] = 2; reply[5] = 0; reply[6] = 19; break;
                case 0x0d: _endpoint = r[4]; break;
                case 0x08: Fill(reply); break;
                case 0x06:
                    _incomingLen = r[4] | r[5] << 8;
                    Assert.Equal(0x12, r[8]);
                    _incoming = [.. r[10..]];
                    break;
                case 0x07: _incoming!.AddRange(r[4..]); break;
                case 0x05:
                    if (_incoming is not null)
                    {
                        LastColors = [.. _incoming.Take(_incomingLen - 2)];
                        Frames++;
                        _incoming = null;
                    }
                    break;
            }
            _replies.Enqueue(reply);
        }
    }

    private void Fill(byte[] reply)
    {
        var data = new List<byte>();
        switch (_endpoint)
        {
            case 0x20:
                reply[4] = 0x0f;
                data.Add((byte)LedCounts.Length);
                foreach (var c in LedCounts) data.AddRange([(byte)(c > 0 ? 2 : 3), 0, (byte)c, 0]);
                break;
            case 0x17:
                reply[4] = 0x06;
                data.Add(7);
                foreach (var rpm in new[] { PumpRpm, 1100, 1100, 1100, 1100, 1100, 1100 }) data.AddRange(BitConverter.GetBytes((ushort)rpm));
                break;
            case 0x21:
                reply[4] = 0x10;
                data.AddRange([1, 0]);
                data.AddRange(BitConverter.GetBytes((ushort)(Coolant * 10)));
                break;
        }
        data.CopyTo(reply, 6);
    }

    public byte[] Read(int timeoutMs)
    {
        lock (_gate) return _replies.Dequeue();
    }

    public void Dispose() => Disposed = true;
}

public class CorsairHubTests
{
    private static void WaitFor(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, $"timed out waiting for: {what}");
            Thread.Sleep(10);
        }
    }

    private static (CorsairHubBackend Backend, FakeHub Hub) Make(bool icue = false)
    {
        var hub = new FakeHub();
        return (new CorsairHubBackend(_ => hub, () => [@"\\?\hid#vid_1b1c&pid_0c32&mi_00#x"], () => icue), hub);
    }

    [Fact]
    public void LayoutPutsThePumpFirstAndPadsEachPortTo34()
    {
        var red = new Rgb(255, 0, 0);
        var data = CommanderCore.Layout(29, [[new Rgb(1, 2, 3)], [red, red], null, null, null, null, [new Rgb(9, 9, 9)]]);
        Assert.Equal((29 + 6 * 34) * 3, data.Length);
        Assert.Equal(new byte[] { 1, 2, 3 }, data[0..3]);
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 0, 0, 0, 0 }, data[(29 * 3)..(31 * 3 + 3)]);
        Assert.Equal(new byte[] { 9, 9, 9 }, data[((29 + 5 * 34) * 3)..((29 + 5 * 34) * 3 + 3)]);
    }

    [Fact]
    public void LongColorBuffersAreSplitAndArriveWhole()
    {
        var hub = new FakeHub();
        using var core = new CommanderCore(hub);
        var data = Enumerable.Range(0, 699).Select(i => (byte)i).ToArray();
        core.SendColors(data);
        Assert.Equal(data, hub.LastColors);
    }

    [Fact]
    public void DiscoverListsThePumpAndEachConnectedFanThenHandsTheHubBack()
    {
        var (backend, hub) = Make();
        hub.LedCounts = [29, 8, 8, 0, 8, 8, 8];
        var devices = backend.Discover(TimeSpan.FromSeconds(1));
        Assert.Equal(["Corsair Pump", "Corsair Fan 1", "Corsair Fan 2", "Corsair Fan 4", "Corsair Fan 5", "Corsair Fan 6"], devices.Select(d => d.Name));
        Assert.Equal(29, devices[0].LedCount);
        Assert.All(devices.Skip(1), d => Assert.Equal(8, d.LedCount));
        Assert.Equal("1B1C:0C32", devices[0].UsbId);
        Assert.False(hub.Awake);
    }

    [Fact]
    public void DiscoverRefusesWhileICueIsRunning()
    {
        var (backend, _) = Make(icue: true);
        var ex = Assert.Throws<BackendException>(() => backend.Discover(TimeSpan.FromSeconds(1)));
        Assert.Contains("iCUE is running", ex.Message);
    }

    [Fact]
    public void SessionsShareOneHubAndOnlyTheLastReleaseHandsItBack()
    {
        var (backend, hub) = Make();
        var devices = backend.Discover(TimeSpan.FromSeconds(1));
        var pump = backend.OpenSession(devices[0]);
        var fan = backend.OpenSession(devices[1]);
        pump.Connect();
        fan.Connect();
        pump.Acquire();
        fan.Acquire();
        pump.Write(Enumerable.Repeat(new Rgb(255, 255, 255), 29).ToArray());
        fan.Write(Enumerable.Repeat(new Rgb(255, 0, 0), 8).ToArray());
        WaitFor(() => hub.LastColors is { } c && c[29 * 3] == 255 && c[0] == 255, "merged frame");
        Assert.Equal(1, hub.Wakes - 1);   // one wake at discovery, one for both sessions

        pump.Release();
        Thread.Sleep(100);
        Assert.True(hub.Awake);
        fan.Release();
        WaitFor(() => !hub.Awake, "hub handed back");
        pump.Dispose();
        fan.Dispose();
        WaitFor(() => hub.Disposed, "hub closed");
    }

    [Fact]
    public void PumpStoppingTripsTheSafetyStopAndHandsTheHubBack()
    {
        var saved = HubLink.SensorSeconds;
        HubLink.SensorSeconds = 0.01;
        try
        {
            var (backend, hub) = Make();
            var devices = backend.Discover(TimeSpan.FromSeconds(1));
            var pump = backend.OpenSession(devices[0]);
            pump.Connect();
            pump.Acquire();
            pump.Write(Enumerable.Repeat(new Rgb(1, 1, 1), 29).ToArray());
            WaitFor(() => hub.Awake, "hub in software mode");
            hub.PumpRpm = 0;
            WaitFor(() => backend.SafetyStop is not null, "safety stop");
            WaitFor(() => !hub.Awake, "hub handed back");
            Assert.Throws<BackendException>(() => pump.Verify(true));
            pump.Dispose();

            var again = backend.OpenSession(devices[0]);
            again.Connect();
            Assert.Throws<BackendException>(again.Acquire);
            again.Dispose();
        }
        finally { HubLink.SensorSeconds = saved; }
    }
}
