using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace PitGlow.OpenRgb;

/// <summary>
/// A minimal, synchronous OpenRGB SDK client with hard timeouts. It never asks OpenRGB to
/// rescan, resize zones or save profiles: those can drop USB devices until a power cycle.
/// </summary>
public sealed class OpenRgbClient : IDisposable
{
    public const uint ClientProtocol = 3;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly object _io = new();

    public uint Protocol { get; private set; }
    public bool DeviceListChanged { get; private set; }

    private OpenRgbClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public static OpenRgbClient Connect(string host, int port, string clientName, TimeSpan timeout)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            var connect = tcp.ConnectAsync(host, port);
            if (!connect.Wait(timeout))
                throw new TimeoutException($"OpenRGB at {host}:{port} did not accept the connection in {timeout.TotalSeconds:0} s");
            if (connect.IsFaulted) throw connect.Exception!.InnerException ?? connect.Exception;
        }
        catch (AggregateException ex) when (ex.InnerException is SocketException se)
        {
            tcp.Dispose();
            throw se;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        var client = new OpenRgbClient(tcp);
        try
        {
            client.Negotiate();
            client.Send(0, Packet.SetClientName, Encoding.UTF8.GetBytes(clientName + "\0"));
            tcp.ReceiveTimeout = (int)Math.Max(1000, timeout.TotalMilliseconds);
            tcp.SendTimeout = (int)Math.Max(1000, timeout.TotalMilliseconds);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private void Negotiate()
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, ClientProtocol);
        Send(0, Packet.RequestProtocolVersion, payload);
        _tcp.ReceiveTimeout = 2000;   // very old servers never answer this; assume version 0
        try
        {
            var reply = Receive(Packet.RequestProtocolVersion);
            uint server = reply.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(reply) : 0;
            Protocol = Math.Min(server, ClientProtocol);
        }
        catch (IOException)
        {
            Protocol = 0;
        }
    }

    public int ControllerCount()
    {
        lock (_io)
        {
            Send(0, Packet.RequestControllerCount, []);
            var data = Receive(Packet.RequestControllerCount);
            return (int)BinaryPrimitives.ReadUInt32LittleEndian(data);
        }
    }

    public ControllerData Controller(int index)
    {
        lock (_io)
        {
            byte[] payload = [];
            if (Protocol >= 1)
            {
                payload = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(payload, Protocol);
            }
            Send((uint)index, Packet.RequestControllerData, payload);
            var data = Receive(Packet.RequestControllerData);
            return ControllerData.Parse(data, Protocol);
        }
    }

    public List<ControllerData> AllControllers()
    {
        int count = ControllerCount();
        var list = new List<ControllerData>(count);
        for (int i = 0; i < count; i++) list.Add(Controller(i));
        DeviceListChanged = false;   // we now hold the current list
        return list;
    }

    public void UpdateLeds(int index, IReadOnlyList<Rgb> colors)
    {
        var packed = colors.Select(ControllerData.Pack).ToList();
        lock (_io) Send((uint)index, Packet.UpdateLeds, ControllerData.UpdateLedsPayload(packed));
    }

    public void UpdateLedsRaw(int index, IReadOnlyList<uint> colors)
    {
        lock (_io) Send((uint)index, Packet.UpdateLeds, ControllerData.UpdateLedsPayload(colors));
    }

    public void SetCustomMode(int index)
    {
        lock (_io) Send((uint)index, Packet.SetCustomMode, []);
    }

    public void UpdateMode(int index, int modeIndex, ModeData mode)
    {
        lock (_io) Send((uint)index, Packet.UpdateMode, ControllerData.UpdateModePayload(modeIndex, mode, Protocol));
    }

    // ---- framing -----------------------------------------------------------------

    private void Send(uint deviceIndex, uint id, ReadOnlySpan<byte> payload)
    {
        var packet = Packet.Build(deviceIndex, id, payload);
        _stream.Write(packet, 0, packet.Length);
    }

    /// <summary>Read packets until the expected reply arrives; note device-list updates on the way.</summary>
    private byte[] Receive(uint expectedId)
    {
        var header = new byte[Packet.HeaderSize];
        while (true)
        {
            ReadExact(header);
            if (!header.AsSpan(0, 4).SequenceEqual(Packet.Magic))
                throw new InvalidDataException("OpenRGB sent data PitGlow doesn't understand (bad packet header)");
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
            if (size > 64 * 1024 * 1024) throw new InvalidDataException("OpenRGB sent an implausibly large packet");
            var body = new byte[size];
            ReadExact(body);
            if (id == expectedId) return body;
            if (id == Packet.DeviceListUpdated) DeviceListChanged = true;
        }
    }

    private void ReadExact(byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = _stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) throw new IOException("OpenRGB closed the connection");
            read += n;
        }
    }

    public void Dispose()
    {
        try { _stream.Dispose(); } catch { /* ignore */ }
        _tcp.Dispose();
    }
}
