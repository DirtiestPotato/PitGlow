using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.RegularExpressions;

namespace PitGlow.Sources;

/// <summary>One consistent read of the iRacing variables PitGlow needs.</summary>
public sealed record IRacingSnapshot(double SessionTime, bool IsOnTrack, bool IsReplayPlaying,
                                     double Rpm, uint SessionFlags, uint EngineWarnings);

/// <summary>The iRacing SDK as PitGlow uses it; swapped for a fake in tests.</summary>
public interface IIRacingSdk
{
    bool IsConnected { get; }
    bool Startup();
    void Shutdown();
    IRacingSnapshot? Read();
    string? SessionInfo();
}

/// <summary>iRacing via the official shared-memory SDK.</summary>
public sealed partial class IRacingSource(IIRacingSdk? sdk = null) : ITelemetrySource
{
    // irsdk_Flags bits
    private const uint Checkered = 0x1, White = 0x2, Green = 0x4, Yellow = 0x8, Red = 0x10, Blue = 0x20, Debris = 0x40;
    private const uint YellowWaving = 0x100, Caution = 0x4000, CautionWaving = 0x8000;
    private const uint Black = 0x10000, Disqualify = 0x20000, Furled = 0x80000, Repair = 0x100000;
    private const uint StartGo = 0x80000000;
    private const uint PitLimiterBit = 0x10;   // irsdk_EngineWarnings.pitSpeedLimiter

    private static readonly (uint Mask, Flag Flag)[] Bits =
    [
        (Checkered, Flag.Checkered), (White, Flag.White), (Green | StartGo, Flag.Green),
        (Yellow | YellowWaving, Flag.Yellow), (Red, Flag.Red), (Blue, Flag.Blue),
        (Debris, Flag.Debris), (Caution | CautionWaving, Flag.FullCourseYellow),
        (Black, Flag.Black), (Disqualify, Flag.Disqualified), (Furled, Flag.BlackWhite),
        (Repair, Flag.Meatball),
    ];

    private readonly IIRacingSdk _sdk = sdk ?? new IRacingSharedMemory();
    private Dictionary<string, double?> _lights = [];
    private double? _infoChecked;
    private double? _lastTick;
    private double _lastChange;
    private double _nextAttempt;

    public string Game => "iRacing";

    public static IReadOnlySet<Flag> FlagsFromBits(uint bits) =>
        Bits.Where(b => (bits & b.Mask) != 0).Select(b => b.Flag).ToHashSet();

    [GeneratedRegex(@"^\s*(DriverCarIdleRPM|DriverCarSLFirstRPM|DriverCarSLShiftRPM|DriverCarSLBlinkRPM|DriverCarRedLine):\s*([-0-9.]+)", RegexOptions.Multiline)]
    private static partial Regex LightPattern();

    public static Dictionary<string, double?> ParseLights(string? yaml)
    {
        var lights = new Dictionary<string, double?>();
        if (yaml is null) return lights;
        foreach (Match m in LightPattern().Matches(yaml))
        {
            if (lights.ContainsKey(m.Groups[1].Value)) continue;   // first car = the player's
            lights[m.Groups[1].Value] =
                double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
        }
        return lights;
    }

    private bool Connect(double now)
    {
        if (_sdk.IsConnected) return true;
        if (now < _nextAttempt) return false;
        _nextAttempt = now + 1.0;
        return _sdk.Startup() && _sdk.IsConnected;
    }

    public Sample? Read(double now)
    {
        if (!Connect(now)) return null;
        var snap = _sdk.Read();
        if (snap is null) return null;
        if (snap.SessionTime != _lastTick)
        {
            _lastTick = snap.SessionTime;
            _lastChange = now;
        }
        if (now - _lastChange > 2 || !snap.IsOnTrack || snap.IsReplayPlaying) return null;
        if (_infoChecked is null || now - _infoChecked > 2)
        {
            _lights = ParseLights(_sdk.SessionInfo());
            _infoChecked = now;
        }
        return new Sample(Game)
        {
            Rpm = snap.Rpm,
            IdleRpm = _lights.GetValueOrDefault("DriverCarIdleRPM"),
            FirstRpm = _lights.GetValueOrDefault("DriverCarSLFirstRPM"),
            ShiftRpm = _lights.GetValueOrDefault("DriverCarSLShiftRPM"),
            BlinkRpm = _lights.GetValueOrDefault("DriverCarSLBlinkRPM"),
            MaxRpm = _lights.GetValueOrDefault("DriverCarRedLine"),
            PitLimiter = (snap.EngineWarnings & PitLimiterBit) != 0,
            Flags = FlagsFromBits(snap.SessionFlags),
        };
    }

    public void Close()
    {
        try { _sdk.Shutdown(); } catch { /* ignore */ }
        _lastTick = null;
        _infoChecked = null;
    }
}

/// <summary>
/// Reads iRacing's memory-mapped telemetry (Local\IRSDKMemMapFileName) directly.
/// Header layout from the iRacing SDK (irsdk_defines.h).
/// </summary>
public sealed class IRacingSharedMemory : IIRacingSdk
{
    private const string MapName = @"Local\IRSDKMemMapFileName";
    private const int VarHeaderSize = 144;
    private const int StatusConnected = 1;

    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;
    private Dictionary<string, (int Type, int Offset)>? _vars;
    private int _varsForHeaderOffset = -1;
    private int _sessionInfoUpdate = -1;
    private string? _sessionInfo;
    private byte[] _buf = [];

    public bool IsConnected => _view is not null && (_view.ReadInt32(4) & StatusConnected) != 0;

    public bool Startup()
    {
        if (_view is null)
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                _map = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
                _view = _map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            }
            catch (Exception)
            {
                Shutdown();
                return false;
            }
        }
        if (!IsConnected)
        {
            Shutdown();   // release the handle so a closed sim's block can go away
            return false;
        }
        LoadVars();
        return true;
    }

    private void LoadVars()
    {
        int numVars = _view!.ReadInt32(24);
        int headerOffset = _view.ReadInt32(28);
        if (_vars is not null && _vars.Count == numVars && _varsForHeaderOffset == headerOffset) return;
        var vars = new Dictionary<string, (int, int)>();
        var nameBytes = new byte[32];
        for (int i = 0; i < numVars; i++)
        {
            long b = headerOffset + (long)i * VarHeaderSize;
            int type = _view.ReadInt32(b);
            int offset = _view.ReadInt32(b + 4);
            _view.ReadArray(b + 16, nameBytes, 0, 32);
            int len = Array.IndexOf(nameBytes, (byte)0);
            var name = Encoding.ASCII.GetString(nameBytes, 0, len < 0 ? 32 : len);
            vars[name] = (type, offset);
        }
        _vars = vars;
        _varsForHeaderOffset = headerOffset;
    }

    public IRacingSnapshot? Read()
    {
        if (!IsConnected || _vars is null) return null;
        var view = _view!;
        int numBuf = Math.Clamp(view.ReadInt32(32), 1, 4);
        int bufLen = view.ReadInt32(36);
        if (bufLen <= 0) return null;
        if (_buf.Length != bufLen) _buf = new byte[bufLen];
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int best = 0, bestTick = int.MinValue;
            for (int i = 0; i < numBuf; i++)
            {
                int tick = view.ReadInt32(48 + i * 16);
                if (tick > bestTick) { bestTick = tick; best = i; }
            }
            int bufOffset = view.ReadInt32(48 + best * 16 + 4);
            view.ReadArray(bufOffset, _buf, 0, bufLen);
            if (view.ReadInt32(48 + best * 16) == bestTick)   // not overwritten while copying
            {
                return new IRacingSnapshot(
                    Get("SessionTime"), Get("IsOnTrack") != 0, Get("IsReplayPlaying") != 0,
                    Get("RPM"), (uint)(long)Get("SessionFlags"), (uint)(long)Get("EngineWarnings"));
            }
        }
        return null;
    }

    private double Get(string name)
    {
        if (!_vars!.TryGetValue(name, out var v) || v.Offset < 0) return 0;
        var span = _buf.AsSpan(v.Offset);
        return v.Type switch
        {
            0 or 1 => span[0],                                              // char, bool
            2 => BitConverter.ToInt32(span),                                 // int
            3 => BitConverter.ToUInt32(span),                                // bitfield
            4 => BitConverter.ToSingle(span),                                // float
            5 => BitConverter.ToDouble(span),                                // double
            _ => 0,
        };
    }

    public string? SessionInfo()
    {
        if (_view is null) return null;
        int update = _view.ReadInt32(12);
        if (update != _sessionInfoUpdate || _sessionInfo is null)
        {
            int len = _view.ReadInt32(16), offset = _view.ReadInt32(20);
            if (len <= 0) return null;
            var bytes = new byte[len];
            _view.ReadArray(offset, bytes, 0, len);
            int end = Array.IndexOf(bytes, (byte)0);
            _sessionInfo = Encoding.Latin1.GetString(bytes, 0, end < 0 ? len : end);
            _sessionInfoUpdate = update;
        }
        return _sessionInfo;
    }

    public void Shutdown()
    {
        _view?.Dispose();
        _map?.Dispose();
        _view = null;
        _map = null;
        _vars = null;
        _sessionInfo = null;
        _sessionInfoUpdate = -1;
    }
}
