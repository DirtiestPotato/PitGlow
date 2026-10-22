using System.Net;
using System.Net.Sockets;
using System.Text;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace PitGlow.Hue;

/// <summary>
/// Hue Entertainment streaming: an encrypted (DTLS 1.2, pre-shared key) UDP stream to the
/// bridge on port 2100. The bridge applies each frame to the lights within ~20 ms, which is
/// what makes flashing flags possible (the normal REST API allows ~10 changes per second).
/// </summary>
public sealed class HueStream : IDisposable
{
    public const int Port = 2100;
    public const double SendHz = 25;   // resend continuously: UDP may drop, the bridge stops after 10 s of silence

    private readonly string _areaId;
    private readonly IReadOnlyList<HueChannel> _channels;
    private readonly object _gate = new();
    private readonly Thread _thread;
    private UdpDatagramTransport? _udp;
    private DtlsTransport? _dtls;
    private Rgb[]? _frame;
    private volatile bool _stopping;
    private byte _sequence;

    public Exception? Failure { get; private set; }

    private HueStream(string areaId, IReadOnlyList<HueChannel> channels)
    {
        _areaId = areaId;
        _channels = channels;
        _thread = new Thread(Loop) { IsBackground = true, Name = "hue stream" };
    }

    /// <summary>Handshake with the bridge (the area must already be started) and begin sending.</summary>
    public static HueStream Open(string ip, string applicationId, string clientKeyHex, string areaId, IReadOnlyList<HueChannel> channels)
    {
        var stream = new HueStream(areaId, channels);
        try
        {
            stream._udp = new UdpDatagramTransport(new IPEndPoint(IPAddress.Parse(ip), Port));
            var crypto = new BcTlsCrypto(new SecureRandom());
            var client = new HuePskClient(crypto, new BasicTlsPskIdentity(applicationId, Convert.FromHexString(clientKeyHex)));
            stream._dtls = new DtlsClientProtocol().Connect(client, stream._udp);
        }
        catch (Exception ex)
        {
            stream.Dispose();
            throw new PitGlow.Backends.BackendException($"Hue streaming handshake failed ({ex.Message})", ex);
        }
        stream._thread.Start();
        return stream;
    }

    /// <summary>Colors in channel order (one per channel).</summary>
    public void Send(IReadOnlyList<Rgb> colors)
    {
        if (Failure is not null) throw new PitGlow.Backends.BackendException($"Hue stream stopped: {Failure.Message}", Failure);
        lock (_gate) _frame = colors.ToArray();
    }

    private void Loop()
    {
        var interval = TimeSpan.FromSeconds(1.0 / SendHz);
        while (!_stopping)
        {
            Rgb[]? frame;
            lock (_gate) frame = _frame;
            if (frame is not null)
            {
                try
                {
                    var msg = Message(_areaId, _sequence++, _channels, frame);
                    _dtls!.Send(msg, 0, msg.Length);
                }
                catch (Exception ex) when (!_stopping)
                {
                    Failure = ex;
                    return;
                }
            }
            Thread.Sleep(interval);
        }
    }

    /// <summary>One HueStream v2 message (RGB color space, 16 bits per color).</summary>
    public static byte[] Message(string areaId, byte sequence, IReadOnlyList<HueChannel> channels, IReadOnlyList<Rgb> colors)
    {
        if (areaId.Length != 36) throw new ArgumentException("Entertainment area id must be a 36-character uuid", nameof(areaId));
        int n = Math.Min(Math.Min(channels.Count, colors.Count), 20);
        var buf = new byte[16 + 36 + 7 * n];
        Encoding.ASCII.GetBytes("HueStream").CopyTo(buf, 0);
        buf[9] = 0x02;          // API version 2.0
        buf[10] = 0x00;
        buf[11] = sequence;
        // 12-13 reserved, 14 = color space (0 = RGB), 15 reserved
        Encoding.ASCII.GetBytes(areaId).CopyTo(buf, 16);
        int o = 52;
        for (int i = 0; i < n; i++)
        {
            var c = colors[i];
            buf[o++] = (byte)channels[i].Id;
            foreach (var v in new[] { c.R, c.G, c.B })
            {
                ushort wide = (ushort)(v * 257);
                buf[o++] = (byte)(wide >> 8);
                buf[o++] = (byte)wide;
            }
        }
        return buf;
    }

    public void Dispose()
    {
        _stopping = true;
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(1));
        try { _dtls?.Close(); } catch { /* bridge may already be gone */ }
        try { _udp?.Close(); } catch { /* ignore */ }
        _dtls = null;
        _udp = null;
    }

    /// <summary>The only suite the bridge offers: TLS_PSK_WITH_AES_128_GCM_SHA256 over DTLS 1.2.</summary>
    private sealed class HuePskClient(TlsCrypto crypto, TlsPskIdentity identity) : PskTlsClient(crypto, identity)
    {
        protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.DTLSv12.Only();

        protected override int[] GetSupportedCipherSuites() => [CipherSuite.TLS_PSK_WITH_AES_128_GCM_SHA256];

        public override int GetHandshakeTimeoutMillis() => 6000;
    }

    /// <summary>A connected UDP socket in the shape BouncyCastle's DTLS wants.</summary>
    private sealed class UdpDatagramTransport : DatagramTransport
    {
        private const int Mtu = 1500 - 20 - 8;   // minus IPv4 and UDP headers
        private readonly Socket _socket;

        public UdpDatagramTransport(IPEndPoint bridge)
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Connect(bridge);
        }

        public int GetReceiveLimit() => Mtu;

        public int GetSendLimit() => Mtu;

        public int Receive(byte[] buf, int off, int len, int waitMillis) => Receive(buf.AsSpan(off, len), waitMillis);

        public int Receive(Span<byte> buffer, int waitMillis)
        {
            int micros = waitMillis <= 0 ? -1 : waitMillis * 1000;
            if (!_socket.Poll(micros, SelectMode.SelectRead)) return -1;
            try
            {
                return _socket.Receive(buffer);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.TimedOut)
            {
                return -1;
            }
        }

        public void Send(byte[] buf, int off, int len) => _socket.Send(buf, off, len, SocketFlags.None);

        public void Send(ReadOnlySpan<byte> buffer) => _socket.Send(buffer);

        public void Close() => _socket.Dispose();
    }
}
