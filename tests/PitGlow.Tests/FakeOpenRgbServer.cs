using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using PitGlow.OpenRgb;

namespace PitGlow.Tests;

/// <summary>One device on the fake server, recording what clients do to it.</summary>
public sealed class FakeController
{
    private readonly object _lock = new();
    public ControllerData Data;
    public readonly List<Rgb[]> Writes = [];
    public readonly List<string> ModeChanges = [];
    public readonly List<string> Violations = [];

    public FakeController(string name, int type, (string Zone, int Leds)[] zones, string[]? modes = null, int active = 0,
                          Rgb? color = null, string location = "")
    {
        modes ??= ["Direct"];
        var c = ControllerData.Pack(color ?? new Rgb(10, 20, 30));
        int total = zones.Sum(z => z.Leds);
        Data = new ControllerData
        {
            Type = type, Name = name, Vendor = "Fake", Description = "fake device", Version = "1", Serial = "",
            Location = location, ActiveMode = active,
            Modes = [.. modes.Select((m, i) => new ModeData
            {
                Name = m, Value = i,
                ColorMode = m == "Direct" ? ColorMode.PerLed : ColorMode.ModeSpecific,
                Colors = m == "Direct" ? Array.Empty<uint>() : new uint[] { 0x00FF00FF },
                ColorsMin = m == "Direct" ? 0u : 1u, ColorsMax = m == "Direct" ? 0u : 1u,
            })],
            Zones = [.. zones.Select(z => new ZoneData(z.Zone, 1, 0, 64, (uint)z.Leds))],
            Leds = [.. Enumerable.Range(0, total).Select(i => new LedData($"LED {i}", (uint)i))],
            Colors = [.. Enumerable.Repeat(c, total)],
        };
    }

    public string ActiveModeName { get { lock (_lock) return Data.ActiveModeName; } }
    public Rgb[] Colors() { lock (_lock) return [.. Data.Colors.Select(ControllerData.Unpack)]; }
    public int WriteCount { get { lock (_lock) return Writes.Count; } }
    public Rgb[]? LastWrite { get { lock (_lock) return Writes.Count > 0 ? Writes[^1] : null; } }
    public List<Rgb[]> WritesCopy() { lock (_lock) return [.. Writes]; }

    public void SetLeds(List<uint> colors)
    {
        lock (_lock)
        {
            var mode = Data.Modes[Data.ActiveMode];
            if (mode.ColorMode != ColorMode.PerLed) Violations.Add($"per-LED write while in {mode.Name}");
            if (colors.Count != Data.Leds.Count) Violations.Add($"wrote {colors.Count} colors to {Data.Leds.Count} LEDs");
            Data = Data with { Colors = colors };
            Writes.Add([.. colors.Select(ControllerData.Unpack)]);
        }
    }

    public void SetMode(int index, ModeData mode)
    {
        lock (_lock)
        {
            var modes = Data.Modes.ToList();
            modes[index] = mode;
            Data = Data with { ActiveMode = index, Modes = modes };
            ModeChanges.Add(mode.Name);
        }
    }

    public void SetCustomMode()
    {
        lock (_lock)
        {
            int i = Data.Modes.ToList().FindIndex(m => m.ColorMode == ColorMode.PerLed);
            Data = Data with { ActiveMode = i };
            ModeChanges.Add(Data.Modes[i].Name);
        }
    }

    /// <summary>Simulate another app (iCUE, the user) switching the device's mode.</summary>
    public void ExternalModeChange(int index)
    {
        lock (_lock) Data = Data with { ActiveMode = index };
    }

    public byte[] Serialize(uint protocol) { lock (_lock) return Data.Serialize(protocol); }
}

/// <summary>A fake OpenRGB SDK server speaking the real wire protocol on localhost.</summary>
public sealed class FakeOpenRgbServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly List<TcpClient> _clients = [];
    private volatile bool _stopping;

    public uint Version { get; set; } = 4;           // OpenRGB 0.9
    public List<FakeController> Devices { get; }
    public int FailNextConnects;                        // close the next N connections at once
    public volatile bool Hang;                          // accept, never answer (stuck detection)
    public int Connections;

    public FakeOpenRgbServer(List<FakeController> devices)
    {
        Devices = devices;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        new Thread(Accept) { IsBackground = true }.Start();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Shaped like the user's rig on OpenRGB 1.1.</summary>
    public static List<FakeController> UserRig() =>
    [
        new("X870 AORUS ELITE WIFI7", 0, [("Chipset Accent", 1), ("LED_C", 1)], location: @"HID: \\?\hid#vid_048d&pid_5711&mi_00#x"),
        new("Corsair Commander Core", 3,
            [("Pump", 29), .. Enumerable.Range(1, 6).Select(i => ($"Corsair Fan Header {i}", 8))],
            location: @"HID: \\?\hid#vid_1b1c&pid_0c32&mi_00#7&abc"),
        new("MSI GeForce RTX 5090 VANGUARD SOC", 2, [("GPU", 1)], modes: ["Rainbow Wave", "Direct"], active: 0,
            location: "I2C: NVIDIA, address 0x68"),
        new("Kingston Fury DDR5 DRAM", 1, [("Fury Slot 1", 12), ("Fury Slot 2", 12)], modes: ["Static", "Direct"],
            location: "I2C: PIIX4 0, address 0x60"),
        new("Razer Naga Classic", 6, [("Logo", 1), ("Scroll Wheel", 1), ("Numpad", 1)], modes: ["Direct", "Static"],
            location: @"HID: \\?\hid#vid_1532&pid_0093&mi_00#x"),
    ];

    private void Accept()
    {
        while (!_stopping)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch { return; }
            Interlocked.Increment(ref Connections);
            if (FailNextConnects > 0)
            {
                FailNextConnects--;
                client.Dispose();
                continue;
            }
            lock (_clients) _clients.Add(client);
            new Thread(() => Serve(client)) { IsBackground = true }.Start();
        }
    }

    private void Serve(TcpClient client)
    {
        uint protocol = 0;
        try
        {
            var stream = client.GetStream();
            var header = new byte[16];
            while (!_stopping)
            {
                if (!ReadExact(stream, header)) return;
                uint dev = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                var body = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12))];
                if (!ReadExact(stream, body)) return;
                if (Hang) continue;
                switch (id)
                {
                    case Packet.RequestProtocolVersion:
                        protocol = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(body), Version);
                        Reply(stream, 0, id, U32(Version));
                        break;
                    case Packet.SetClientName:
                        break;
                    case Packet.RequestControllerCount:
                        // Exercise the client's handling of unsolicited packets.
                        stream.Write(Packet.Build(0, Packet.DeviceListUpdated, []));
                        Reply(stream, 0, id, U32((uint)Devices.Count));
                        break;
                    case Packet.RequestControllerData:
                        uint requested = body.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(body) : 0;
                        Reply(stream, dev, id, Devices[(int)dev].Serialize(Math.Min(requested, Version)));
                        break;
                    case Packet.UpdateLeds:
                        Devices[(int)dev].SetLeds(ControllerData.ParseUpdateLeds(body));
                        break;
                    case Packet.SetCustomMode:
                        Devices[(int)dev].SetCustomMode();
                        break;
                    case Packet.UpdateMode:
                        var (index, mode) = ControllerData.ParseUpdateMode(body, protocol);
                        Devices[(int)dev].SetMode(index, mode);
                        break;
                }
            }
        }
        catch
        {
            // client went away
        }
        finally
        {
            client.Dispose();
        }
    }

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    private static void Reply(NetworkStream s, uint dev, uint id, byte[] payload) => s.Write(Packet.Build(dev, id, payload));

    private static bool ReadExact(NetworkStream s, byte[] buf)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = s.Read(buf, read, buf.Length - read);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    public void Dispose()
    {
        _stopping = true;
        _listener.Stop();
        lock (_clients) foreach (var c in _clients) c.Dispose();
    }
}

internal static class Wait
{
    public static bool For(Func<bool> cond, double timeoutSeconds = 3.0)
    {
        var end = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < end)
        {
            if (cond()) return true;
            Thread.Sleep(10);
        }
        return cond();
    }
}
