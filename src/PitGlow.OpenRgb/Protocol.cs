using System.Buffers.Binary;
using System.Text;

namespace PitGlow.OpenRgb;

/// <summary>
/// OpenRGB SDK wire format (TCP, default port 6742). See OpenRGB's Documentation/OpenRGBSDK.md.
/// PitGlow speaks protocol version 3: every server since OpenRGB 0.6 answers in that format,
/// including 0.9 (v4) and 1.x (v5), because the server uses the lower of the two versions.
/// </summary>
public static class Packet
{
    public const uint RequestControllerCount = 0;
    public const uint RequestControllerData = 1;
    public const uint RequestProtocolVersion = 40;
    public const uint SetClientName = 50;
    public const uint DeviceListUpdated = 100;
    public const uint UpdateLeds = 1050;
    public const uint SetCustomMode = 1100;
    public const uint UpdateMode = 1101;

    public const int HeaderSize = 16;
    public static ReadOnlySpan<byte> Magic => "ORGB"u8;

    public static byte[] Build(uint deviceIndex, uint id, ReadOnlySpan<byte> payload)
    {
        var buf = new byte[HeaderSize + payload.Length];
        Magic.CopyTo(buf);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), deviceIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), id);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12), (uint)payload.Length);
        payload.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }
}

public enum ColorMode : uint { None = 0, PerLed = 1, ModeSpecific = 2, Random = 3 }

public sealed record ModeData
{
    public string Name { get; init; } = "";
    public int Value { get; init; }
    public uint Flags { get; init; }
    public uint SpeedMin { get; init; }
    public uint SpeedMax { get; init; }
    public uint BrightnessMin { get; init; }
    public uint BrightnessMax { get; init; }
    public uint ColorsMin { get; init; }
    public uint ColorsMax { get; init; }
    public uint Speed { get; init; }
    public uint Brightness { get; init; }
    public uint Direction { get; init; }
    public ColorMode ColorMode { get; init; }
    public IReadOnlyList<uint> Colors { get; init; } = [];
}

public sealed record ZoneData(string Name, int Type, uint LedsMin, uint LedsMax, uint LedsCount,
                              uint MatrixHeight = 0, uint MatrixWidth = 0, IReadOnlyList<uint>? Matrix = null);

public sealed record LedData(string Name, uint Value);

/// <summary>One controller (device) as the SDK describes it.</summary>
public sealed record ControllerData
{
    public int Type { get; init; }
    public string Name { get; init; } = "";
    public string Vendor { get; init; } = "";
    public string Description { get; init; } = "";
    public string Version { get; init; } = "";
    public string Serial { get; init; } = "";
    public string Location { get; init; } = "";
    public int ActiveMode { get; init; }
    public IReadOnlyList<ModeData> Modes { get; init; } = [];
    public IReadOnlyList<ZoneData> Zones { get; init; } = [];
    public IReadOnlyList<LedData> Leds { get; init; } = [];
    public IReadOnlyList<uint> Colors { get; init; } = [];

    public string ActiveModeName => ActiveMode >= 0 && ActiveMode < Modes.Count ? Modes[ActiveMode].Name : "";

    public int FindMode(string name)
    {
        for (int i = 0; i < Modes.Count; i++)
            if (string.Equals(Modes[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    // ---- parsing -------------------------------------------------------------

    public static ControllerData Parse(ReadOnlySpan<byte> data, uint protocol)
    {
        var r = new Reader(data);
        r.U32();                                    // data_size
        int type = r.I32();
        string name = r.Str();
        string vendor = protocol >= 1 ? r.Str() : "";
        string description = r.Str();
        string version = r.Str();
        string serial = r.Str();
        string location = r.Str();

        int numModes = r.U16();
        int activeMode = r.I32();
        var modes = new List<ModeData>(numModes);
        for (int i = 0; i < numModes; i++) modes.Add(ReadMode(ref r, protocol));

        int numZones = r.U16();
        var zones = new List<ZoneData>(numZones);
        for (int i = 0; i < numZones; i++)
        {
            string zname = r.Str();
            int ztype = r.I32();
            uint min = r.U32(), max = r.U32(), count = r.U32();
            int matrixLen = r.U16();
            uint h = 0, w = 0;
            List<uint>? matrix = null;
            if (matrixLen > 0)
            {
                h = r.U32();
                w = r.U32();
                matrix = new List<uint>((int)(h * w));
                for (int k = 0; k < h * w; k++) matrix.Add(r.U32());
            }
            if (protocol >= 4)
            {
                int numSegments = r.U16();
                for (int s = 0; s < numSegments; s++)
                {
                    r.Str();
                    r.I32();
                    r.U32();
                    r.U32();
                }
            }
            zones.Add(new ZoneData(zname, ztype, min, max, count, h, w, matrix));
        }

        int numLeds = r.U16();
        var leds = new List<LedData>(numLeds);
        for (int i = 0; i < numLeds; i++) leds.Add(new LedData(r.Str(), r.U32()));

        int numColors = r.U16();
        var colors = new List<uint>(numColors);
        for (int i = 0; i < numColors; i++) colors.Add(r.U32());

        return new ControllerData
        {
            Type = type, Name = name, Vendor = vendor, Description = description, Version = version,
            Serial = serial, Location = location, ActiveMode = activeMode, Modes = modes,
            Zones = zones, Leds = leds, Colors = colors,
        };
    }

    private static ModeData ReadMode(ref Reader r, uint protocol)
    {
        string name = r.Str();
        int value = r.I32();
        uint flags = r.U32(), speedMin = r.U32(), speedMax = r.U32();
        uint brightnessMin = 0, brightnessMax = 0;
        if (protocol >= 3)
        {
            brightnessMin = r.U32();
            brightnessMax = r.U32();
        }
        uint colorsMin = r.U32(), colorsMax = r.U32(), speed = r.U32();
        uint brightness = protocol >= 3 ? r.U32() : 0;
        uint direction = r.U32();
        var colorMode = (ColorMode)r.U32();
        int numColors = r.U16();
        var colors = new List<uint>(numColors);
        for (int i = 0; i < numColors; i++) colors.Add(r.U32());
        return new ModeData
        {
            Name = name, Value = value, Flags = flags, SpeedMin = speedMin, SpeedMax = speedMax,
            BrightnessMin = brightnessMin, BrightnessMax = brightnessMax, ColorsMin = colorsMin,
            ColorsMax = colorsMax, Speed = speed, Brightness = brightness, Direction = direction,
            ColorMode = colorMode, Colors = colors,
        };
    }

    // ---- writing (used for UpdateMode and by the test server) ----------------

    public byte[] Serialize(uint protocol)
    {
        var w = new Writer();
        w.U32(0);                                   // data_size, patched below
        w.I32(Type);
        w.Str(Name);
        if (protocol >= 1) w.Str(Vendor);
        w.Str(Description);
        w.Str(Version);
        w.Str(Serial);
        w.Str(Location);
        w.U16(Modes.Count);
        w.I32(ActiveMode);
        foreach (var m in Modes) WriteMode(w, m, protocol);
        w.U16(Zones.Count);
        foreach (var z in Zones)
        {
            w.Str(z.Name);
            w.I32(z.Type);
            w.U32(z.LedsMin);
            w.U32(z.LedsMax);
            w.U32(z.LedsCount);
            if (z.Matrix is { Count: > 0 } matrix)
            {
                w.U16(8 + matrix.Count * 4);
                w.U32(z.MatrixHeight);
                w.U32(z.MatrixWidth);
                foreach (var v in matrix) w.U32(v);
            }
            else w.U16(0);
            if (protocol >= 4) w.U16(0);            // no segments
        }
        w.U16(Leds.Count);
        foreach (var l in Leds)
        {
            w.Str(l.Name);
            w.U32(l.Value);
        }
        w.U16(Colors.Count);
        foreach (var c in Colors) w.U32(c);
        var bytes = w.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)bytes.Length);
        return bytes;
    }

    internal static void WriteMode(Writer w, ModeData m, uint protocol)
    {
        w.Str(m.Name);
        w.I32(m.Value);
        w.U32(m.Flags);
        w.U32(m.SpeedMin);
        w.U32(m.SpeedMax);
        if (protocol >= 3)
        {
            w.U32(m.BrightnessMin);
            w.U32(m.BrightnessMax);
        }
        w.U32(m.ColorsMin);
        w.U32(m.ColorsMax);
        w.U32(m.Speed);
        if (protocol >= 3) w.U32(m.Brightness);
        w.U32(m.Direction);
        w.U32((uint)m.ColorMode);
        w.U16(m.Colors.Count);
        foreach (var c in m.Colors) w.U32(c);
    }

    /// <summary>Payload of an UpdateMode packet: data_size, mode index, mode description.</summary>
    public static byte[] UpdateModePayload(int modeIndex, ModeData mode, uint protocol)
    {
        var w = new Writer();
        w.U32(0);
        w.I32(modeIndex);
        WriteMode(w, mode, protocol);
        var bytes = w.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)bytes.Length);
        return bytes;
    }

    public static (int ModeIndex, ModeData Mode) ParseUpdateMode(ReadOnlySpan<byte> payload, uint protocol)
    {
        var r = new Reader(payload);
        r.U32();
        int index = r.I32();
        return (index, ReadMode(ref r, protocol));
    }

    /// <summary>Payload of an UpdateLeds packet: data_size, count, colors.</summary>
    public static byte[] UpdateLedsPayload(IReadOnlyList<uint> colors)
    {
        var w = new Writer();
        w.U32((uint)(4 + 2 + 4 * colors.Count));
        w.U16(colors.Count);
        foreach (var c in colors) w.U32(c);
        return w.ToArray();
    }

    public static List<uint> ParseUpdateLeds(ReadOnlySpan<byte> payload)
    {
        var r = new Reader(payload);
        r.U32();
        int n = r.U16();
        var list = new List<uint>(n);
        for (int i = 0; i < n; i++) list.Add(r.U32());
        return list;
    }

    /// <summary>OpenRGB packs colors as 0x00BBGGRR.</summary>
    public static uint Pack(PitGlow.Rgb c) => (uint)(c.R | (c.G << 8) | (c.B << 16));
    public static PitGlow.Rgb Unpack(uint v) => new((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));
}

internal ref struct Reader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _pos = 0;

    private ReadOnlySpan<byte> Take(int n)
    {
        if (_pos + n > _data.Length)
            throw new InvalidDataException("OpenRGB sent a shorter controller description than expected");
        var s = _data.Slice(_pos, n);
        _pos += n;
        return s;
    }

    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public string Str()
    {
        int len = U16();
        var bytes = Take(len);
        if (len > 0 && bytes[len - 1] == 0) bytes = bytes[..(len - 1)];
        return Encoding.UTF8.GetString(bytes);
    }
}

internal sealed class Writer
{
    private readonly MemoryStream _ms = new();
    private readonly byte[] _tmp = new byte[4];

    public void U32(uint v)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_tmp, v);
        _ms.Write(_tmp, 0, 4);
    }

    public void I32(int v)
    {
        BinaryPrimitives.WriteInt32LittleEndian(_tmp, v);
        _ms.Write(_tmp, 0, 4);
    }

    public void U16(int v)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_tmp, (ushort)v);
        _ms.Write(_tmp, 0, 2);
    }

    public void Str(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        U16(bytes.Length + 1);
        _ms.Write(bytes);
        _ms.WriteByte(0);
    }

    public byte[] ToArray() => _ms.ToArray();
}
