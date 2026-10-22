using PitGlow.Backends;
using PitGlow.Devices;

namespace PitGlow.OpenRgb;

/// <summary>Lighting through OpenRGB's SDK server.</summary>
public sealed class OpenRgbBackend(string host = "127.0.0.1", int port = 6742) : IBackend
{
    public static TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    public string Id => "openrgb";
    public string DisplayName => "OpenRGB";
    public string Host => host;
    public int Port => port;

    public BackendStatus Probe(TimeSpan timeout)
    {
        try
        {
            var count = WithTimeout(() =>
            {
                using var c = OpenRgbClient.Connect(host, port, "Potato PitGlow (probe)", ConnectTimeout);
                return (c.ControllerCount(), c.Protocol);
            }, timeout);
            return new BackendStatus(true, $"OpenRGB SDK at {host}:{port}, protocol {count.Item2}, {count.Item1} devices");
        }
        catch (Exception ex)
        {
            return new BackendStatus(false, Explain(ex));
        }
    }

    public IReadOnlyList<DeviceInfo> Discover(TimeSpan timeout)
    {
        try
        {
            return WithTimeout(() =>
            {
                using var c = OpenRgbClient.Connect(host, port, "Potato PitGlow (scan)", ConnectTimeout);
                return Describe(c.AllControllers());
            }, timeout);
        }
        catch (BackendException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new BackendException(Explain(ex), ex);
        }
    }

    public IDeviceSession OpenSession(DeviceInfo device) => new OpenRgbSession(this, device);

    internal OpenRgbClient NewClient(string name) => OpenRgbClient.Connect(host, port, name, ConnectTimeout);

    /// <summary>Turn OpenRGB controllers into the shared device shape.</summary>
    public IReadOnlyList<DeviceInfo> Describe(IReadOnlyList<ControllerData> controllers)
    {
        var seen = new Dictionary<string, int>();
        var output = new List<DeviceInfo>();
        for (int position = 0; position < controllers.Count; position++)
        {
            var c = controllers[position];
            int occurrence = seen.GetValueOrDefault(c.Name);
            seen[c.Name] = occurrence + 1;
            var zones = new List<LedZone>();
            int start = 0;
            for (int z = 0; z < c.Zones.Count; z++)
            {
                int count = (int)c.Zones[z].LedsCount;
                zones.Add(new LedZone(c.Zones[z].Name, z, Enumerable.Range(start, count).ToArray()));
                start += count;
            }
            output.Add(new DeviceInfo
            {
                Backend = Id,
                Name = c.Name,
                Occurrence = occurrence,
                Position = position,
                Kind = c.Type is >= 0 and < (int)DeviceKind.Unknown ? (DeviceKind)c.Type : DeviceKind.Unknown,
                Location = c.Location,
                Serial = c.Serial,
                LedCount = c.Leds.Count,
                Zones = zones,
                OriginalColors = [.. c.Colors.Select(ControllerData.Unpack)],
                OriginalMode = c.ActiveModeName,
                ModeNames = [.. c.Modes.Select(m => m.Name)],
                BackendState = c,
            });
        }
        return output;
    }

    private static T WithTimeout<T>(Func<T> work, TimeSpan timeout)
    {
        var task = Task.Run(work);
        if (!task.Wait(timeout))
            throw new BackendException(
                $"OpenRGB is not responding (waited {timeout.TotalSeconds:0} s). It is usually still detecting devices, " +
                "or one device's driver has hung. Wait for detection to finish; if it never does, power-cycle the PC.");
        return task.Result;
    }

    internal static string Explain(Exception ex)
    {
        var inner = ex is AggregateException agg ? agg.Flatten().InnerException ?? ex : ex;
        return inner switch
        {
            BackendException b => b.Message,
            System.Net.Sockets.SocketException => "Can't reach OpenRGB. Open OpenRGB, go to the SDK Server tab and click Start Server.",
            TimeoutException t => t.Message,
            _ => $"OpenRGB error: {inner.Message}",
        };
    }
}
