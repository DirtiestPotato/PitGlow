using PitGlow.Backends;

namespace PitGlow.CorsairHub;

/// <summary>Raw reports to and from the hub; swapped for a fake in tests.</summary>
public interface IHubTransport : IDisposable
{
    /// <summary>Output report size including the leading report id byte.</summary>
    int ReportLength { get; }
    void Write(byte[] report);
    /// <summary>Next input report (report id byte first). Throws on timeout.</summary>
    byte[] Read(int timeoutMs);
}

/// <summary>Fan and pump speeds plus coolant temperature, as the hub reports them.</summary>
public sealed record HubSensors(int PumpRpm, IReadOnlyList<int> FanRpm, double? CoolantC);

/// <summary>
/// Corsair Commander Core protocol (the hub bundled with iCUE Elite Capellix coolers), from
/// liquidctl's protocol notes and OpenRGB's driver. "Wake" puts the hub in software mode, where
/// it takes our colors but keeps running the fan and pump curves stored in its memory; "sleep"
/// hands it back to its own lighting. PitGlow never sends fan or pump speeds.
/// </summary>
public sealed class CommanderCore(IHubTransport io) : IDisposable
{
    /// <summary>LED slots reserved per fan port in the color buffer, whatever is plugged in.</summary>
    public const int PortSlots = 34;
    public const int Ports = 6;

    private static readonly byte[] Wake_ = [0x01, 0x03, 0x00, 0x02];
    private static readonly byte[] Sleep_ = [0x01, 0x03, 0x00, 0x01];
    private static readonly byte[] Firmware_ = [0x02, 0x13];
    private static readonly byte[] Open = [0x0d, 0x00];
    private static readonly byte[] ReadCmd = [0x08, 0x00];
    private static readonly byte[] Close = [0x05, 0x01, 0x00];
    private static readonly byte[] WriteFirst = [0x06, 0x00];
    private static readonly byte[] WriteMore = [0x07, 0x00];

    public string Firmware()
    {
        var r = Command(Firmware_);
        return $"{r[4]}.{r[5]}.{r[6]}";
    }

    public void Wake() => Command(Wake_);

    public void Sleep() => Command(Sleep_);

    /// <summary>LED count per channel (0 = pump ring, 1-6 = fan ports); 0 when nothing is connected.</summary>
    public int[] LedCounts()
    {
        var d = ReadEndpoint(0x20, 0x0f);
        int n = Math.Min((int)d[0], 1 + Ports);
        var counts = new int[1 + Ports];
        for (int c = 0; c < n; c++)
        {
            int mode = BitConverter.ToUInt16(d, 1 + c * 4);
            counts[c] = mode == 2 ? BitConverter.ToUInt16(d, 3 + c * 4) : 0;
        }
        return counts;
    }

    public HubSensors Sensors()
    {
        var rpm = ReadEndpoint(0x17, 0x06);
        var speeds = Enumerable.Range(0, rpm[0]).Select(i => (int)BitConverter.ToUInt16(rpm, 1 + i * 2)).ToArray();
        var t = ReadEndpoint(0x21, 0x10);
        double? coolant = t[0] > 0 && t[1] == 0 ? BitConverter.ToUInt16(t, 2) / 10.0 : null;
        return new HubSensors(speeds.ElementAtOrDefault(0), speeds.Skip(1).ToArray(), coolant);
    }

    /// <summary>Lay out colors the way the hub reads them: pump ring first, then every port padded to 34.</summary>
    public static byte[] Layout(int pumpLeds, IReadOnlyList<IReadOnlyList<Rgb>?> channels)
    {
        var data = new byte[(pumpLeds + Ports * PortSlots) * 3];
        for (int c = 0; c < channels.Count && c <= Ports; c++)
        {
            var colors = channels[c];
            if (colors is null) continue;
            int start = c == 0 ? 0 : pumpLeds + (c - 1) * PortSlots;
            int max = c == 0 ? pumpLeds : PortSlots;
            for (int i = 0; i < Math.Min(colors.Count, max); i++)
            {
                data[(start + i) * 3] = colors[i].R;
                data[(start + i) * 3 + 1] = colors[i].G;
                data[(start + i) * 3 + 2] = colors[i].B;
            }
        }
        return data;
    }

    /// <summary>Send one full color buffer (from <see cref="Layout"/>).</summary>
    public void SendColors(byte[] data)
    {
        Command(Open, [0x22]);
        int len = data.Length + 2;
        var head = new byte[] { (byte)len, (byte)(len >> 8), 0, 0, 0x12, 0x00 };
        int firstCap = io.ReportLength - 4 - head.Length, moreCap = io.ReportLength - 4;
        Command(WriteFirst, [.. head, .. data.Take(firstCap)]);
        for (int off = firstCap; off < data.Length; off += moreCap)
            Command(WriteMore, data.Skip(off).Take(moreCap).ToArray());
        Command(Close);
    }

    private byte[] ReadEndpoint(byte endpoint, byte type)
    {
        Command(Open, [endpoint]);
        var r = Command(ReadCmd);
        Command(Close);
        // [0] report id, [1] 0, [2] command echo, [3] status, [4..5] data type, [6..] data
        if (r[4] != type || r[5] != 0) throw new BackendException($"Corsair hub: unexpected reply from endpoint {endpoint:X2}");
        return r[6..];
    }

    private byte[] Command(byte[] cmd, byte[]? data = null)
    {
        var buf = new byte[io.ReportLength];
        buf[1] = 0x08;
        cmd.CopyTo(buf, 2);
        data?.CopyTo(buf, 2 + cmd.Length);
        io.Write(buf);
        for (int i = 0; i < 20; i++)
        {
            var r = io.Read(1000);
            if (r.Length > 2 && r[1] == 0x00 && r[2] == cmd[0]) return r;   // skip unrelated reports
        }
        throw new BackendException("Corsair hub: no reply");
    }

    public void Dispose() => io.Dispose();
}
