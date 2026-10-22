using PitGlow.Devices;

namespace PitGlow.Backends;

/// <summary>
/// All lighting routes in use, in priority order. Every device ends up owned by exactly one
/// route: a vendor SDK first (it is the vendor's own driver), OpenRGB for everything else.
/// A route that is missing or broken is reported and skipped; the others carry on.
/// </summary>
public sealed class BackendSet(IReadOnlyList<IBackend> backends)
{
    /// <summary>USB vendor ids a vendor route takes over from OpenRGB once it has found devices.</summary>
    public static readonly IReadOnlyDictionary<string, string> VendorOf = new Dictionary<string, string>
    {
        ["icue"] = "1B1C",    // Corsair
        ["razer"] = "1532",   // Razer
    };

    public IReadOnlyList<IBackend> Backends => backends;

    public IBackend? Get(string id) => backends.FirstOrDefault(b => b.Id == id);

    public IDeviceSession OpenSession(DeviceInfo info) =>
        (Get(info.Backend) ?? throw new BackendException($"{info.Label}: lighting route \"{info.Backend}\" is not active")).OpenSession(info);

    public sealed record Result(IReadOnlyList<DeviceInfo> Devices, IReadOnlyList<string> Notes, IReadOnlyList<string> Failed);

    /// <summary>Discover every route, each within its own time limit, and settle ownership.</summary>
    public Result Discover(TimeSpan timeout)
    {
        var found = new List<(IBackend Backend, IReadOnlyList<DeviceInfo> Devices)>();
        var notes = new List<string>();
        var failed = new List<string>();
        foreach (var backend in backends)
        {
            try
            {
                var devices = backend.Discover(timeout);
                found.Add((backend, devices));
                notes.Add($"{backend.DisplayName}: {devices.Count} device(s)");
            }
            catch (Exception ex)
            {
                failed.Add(backend.Id);
                notes.Add($"{backend.DisplayName}: not used ({(ex is BackendException ? ex.Message : ex.GetType().Name + ": " + ex.Message)})");
            }
        }

        // Vendors whose own route found devices: OpenRGB leaves those to it.
        var claimed = found.Where(f => f.Devices.Count > 0 && VendorOf.ContainsKey(f.Backend.Id))
                           .Select(f => VendorOf[f.Backend.Id]).ToHashSet();
        var devicesOut = new List<DeviceInfo>();
        foreach (var (backend, devices) in found)
        {
            foreach (var d in devices)
            {
                if (backend.Id == "openrgb" && d.UsbId is string usb && claimed.Contains(usb[..4]))
                {
                    notes.Add($"{d.Label}: left to the vendor's own route instead of OpenRGB");
                    continue;
                }
                devicesOut.Add(d);
            }
        }
        return new Result(devicesOut, notes, failed);
    }
}
