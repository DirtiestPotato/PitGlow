using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using PitGlow.Engine;

namespace PitGlow.Sources;

/// <summary>UDP telemetry: Wreckfest 2 ("Pino" format) and BeamNG.drive (OutGauge).</summary>
public abstract class UdpSource(int port, string host) : ITelemetrySource
{
    public const double StaleSeconds = 1.0;

    private Socket? _sock;
    private Sample? _latest;
    private double _received;
    private readonly byte[] _buffer = new byte[4096];

    public abstract string Game { get; }
    public string? Error { get; protected set; }
    public int Port => port;
    /// <summary>When the last valid packet arrived (any packet, driving or not); null before the first.</summary>
    protected double? LastPacket { get; private set; }

    private void Drain(double now)
    {
        if (_sock is null)
        {
            try
            {
                var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                if (OperatingSystem.IsWindows())
                {
                    // Ignore "port unreachable" ICMP replies, which otherwise surface as recv errors.
                    const int SIO_UDP_CONNRESET = -1744830452;
                    sock.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
                }
                sock.Bind(new IPEndPoint(IPAddress.Parse(host), port));
                sock.Blocking = false;
                _sock = sock;
                Error = null;
            }
            catch (SocketException ex)   // port already used by another app (e.g. SimHub)
            {
                Error = $"{Game}: cannot listen on UDP {port}: {ex.Message}";
                return;
            }
        }
        while (true)
        {
            int n;
            try
            {
                if (_sock.Available <= 0) break;
                n = _sock.Receive(_buffer);
            }
            catch (SocketException)
            {
                break;
            }
            var packet = Parse(_buffer.AsSpan(0, n));
            if (packet is not null)
            {
                _latest = packet;
                _received = now;
                LastPacket = now;
            }
        }
    }

    public virtual Sample? Read(double now)
    {
        Drain(now);
        if (_latest is null || now - _received > StaleSeconds) return null;
        return _latest;
    }

    public abstract Sample? Parse(ReadOnlySpan<byte> data);

    public void Close()
    {
        _sock?.Dispose();
        _sock = null;
        _latest = null;
    }
}

/// <summary>
/// Wreckfest 2. Enable in Documents\My Games\Wreckfest 2\&lt;id&gt;\savegame\telemetry\config.json.
/// Packet layout: TelemetryDataFormatPino.h (pack 1), Main packet = type 0.
/// </summary>
public sealed class Wreckfest2Source(int port = 23124, string host = "127.0.0.1", Wreckfest2Config? keeper = null, Func<bool>? gameRunning = null) : UdpSource(port, host)
{
    public const uint Signature = 1869769584;
    public const int MainMinSize = 1092;
    private const byte Paused = 0x1, Replay = 0x2, Spectate = 0x4;

    private static readonly (int Mask, Flag Flag)[] Marshal =
    [
        (1 << 0, Flag.Green), (1 << 1, Flag.White), (1 << 2, Flag.Checkered), (1 << 3, Flag.Disqualified),
        (1 << 4, Flag.Meatball), (1 << 5, Flag.BlackWhite), (1 << 6, Flag.Blue), (1 << 7, Flag.White),
    ];

    /// <summary>Marker for "game is sending, but the player isn't driving".</summary>
    internal static readonly Sample NotDriving = new("");

    public override string Game => "Wreckfest 2";

    public static IReadOnlySet<Flag> MarshalFlags(int bits) =>
        Marshal.Where(m => (bits & m.Mask) != 0).Select(m => m.Flag).ToHashSet();

    public override Sample? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < MainMinSize) return null;
        uint signature = BinaryPrimitives.ReadUInt32LittleEndian(data);
        byte packetType = data[4];
        byte status = data[5];
        if (signature != Signature || packetType != 0) return null;
        if ((status & (Paused | Replay | Spectate)) != 0) return NotDriving;
        int marshal = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
        int rpm = BinaryPrimitives.ReadInt32LittleEndian(data[435..]);
        int rpmMax = BinaryPrimitives.ReadInt32LittleEndian(data[439..]);
        int redline = BinaryPrimitives.ReadInt32LittleEndian(data[443..]);
        return new Sample(Game)
        {
            Rpm = rpm,
            ShiftRpm = redline > 0 ? redline : null,
            BlinkRpm = rpmMax > 0 ? rpmMax : null,
            MaxRpm = rpmMax > 0 ? rpmMax : null,
            Flags = MarshalFlags(marshal),
        };
    }

    /// <summary>Seconds the game may run without sending anything before PitGlow says so.</summary>
    public const double SilentGameSeconds = 20.0;

    private readonly Func<bool> _gameRunning = gameRunning ?? (() => System.Diagnostics.Process.GetProcessesByName("Wreckfest2").Length > 0);
    private double _nextProcessCheck;
    private double? _runningSince;

    public override Sample? Read(double now)
    {
        keeper?.Check(now);   // motion software may have rewritten the game's telemetry list
        var sample = base.Read(now);
        CheckSilence(now);
        return ReferenceEquals(sample, NotDriving) ? null : sample;
    }

    /// <summary>
    /// The game reads its telemetry list once at startup. If motion software rewrote the list just
    /// then, the game runs without sending PitGlow anything: say so instead of staying dark.
    /// </summary>
    private void CheckSilence(double now)
    {
        if (now < _nextProcessCheck) return;
        _nextProcessCheck = now + 2.0;
        if (!_gameRunning())
        {
            _runningSince = null;
            if (Error == SilentWarning) Error = null;
            return;
        }
        _runningSince ??= now;
        bool silent = LastPacket is null || LastPacket < _runningSince;
        if (silent && now - _runningSince > SilentGameSeconds)
        {
            if (Error != SilentWarning) Log.Warn(SilentWarning);
            Error = SilentWarning;
        }
        else if (!silent && Error == SilentWarning) Error = null;
    }

    public const string SilentWarning =
        "Wreckfest 2 is running but no telemetry reaches PitGlow: forward it from your motion software to 127.0.0.1:23124, or enable an entry for that port (see README)";
}

/// <summary>BeamNG.drive OutGauge (Options &gt; Other &gt; Protocols &gt; OutGauge UDP).</summary>
public sealed class BeamNGSource(int port = 4444, string host = "127.0.0.1") : UdpSource(port, host)
{
    private const uint DlShift = 1 << 0, DlPitSpeed = 1 << 3;

    public override string Game => "BeamNG.drive";

    public override Sample? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 92) return null;
        float rpm = BinaryPrimitives.ReadSingleLittleEndian(data[16..]);
        uint showLights = BinaryPrimitives.ReadUInt32LittleEndian(data[44..]);
        return new Sample(Game)
        {
            Rpm = rpm,
            ShiftNow = (showLights & DlShift) != 0,
            PitLimiter = (showLights & DlPitSpeed) != 0,
            HasFlags = false,
        };
    }
}
