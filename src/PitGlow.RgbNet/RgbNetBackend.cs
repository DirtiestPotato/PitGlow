using PitGlow.Backends;
using PitGlow.Devices;
using PitGlow.Engine;
using RGB.NET.Core;
using RGB.NET.Devices.Corsair;
using RGB.NET.Devices.Razer;
using Rgb = PitGlow.Rgb;

namespace PitGlow.RgbNet;

/// <summary>
/// A vendor's own lighting SDK, reached through RGB.NET. The vendor app (iCUE, Synapse) keeps
/// control of the lighting while nobody is racing; PitGlow takes it over (exclusively, where the
/// SDK allows) while at least one device is in use, and hands it back afterwards by closing
/// the SDK session, which brings the vendor app's own effects back.
/// </summary>
public sealed class RgbNetBackend : IBackend
{
    private readonly Func<bool, AbstractRGBDeviceProvider> _providerFor;
    private readonly object _gate = new();
    private AbstractRGBDeviceProvider? _provider;
    private AbstractRGBDeviceProvider? _subscribed;
    private bool _exclusive;
    private int _acquired;
    private volatile bool _initializing;
    private readonly UpdateErrors _errors = new();
    private Timer? _logTimer;
    private ICueLogWatcher? _logWatcher;
    private double _lastLogReconnect = double.NegativeInfinity;

    /// <summary>Least time between reconnects triggered by iCUE's log, so it can never loop.</summary>
    public static double LogReconnectGapSeconds { get; set; } = 20.0;

    private RgbNetBackend(string id, string displayName, Func<bool, AbstractRGBDeviceProvider> providerFor)
    {
        Id = id;
        DisplayName = displayName;
        _providerFor = providerFor;
    }

    public string Id { get; }
    public string DisplayName { get; }

    /// <summary>Corsair devices through iCUE. The SDK dll ships next to PitGlow.exe in x64\.</summary>
    public static RgbNetBackend Corsair() => new("icue", "Corsair iCUE", exclusive =>
    {
        var dir = AppContext.BaseDirectory;
        foreach (var path in new[] { Path.Combine(dir, "x64", "iCUESDK.x64_2019.dll"), Path.Combine(dir, "iCUESDK.x64_2019.dll") })
            if (!CorsairDeviceProvider.PossibleX64NativePaths.Contains(path))
                CorsairDeviceProvider.PossibleX64NativePaths.Insert(0, path);
        CorsairDeviceProvider.ExclusiveAccess = exclusive;
        CorsairDeviceProvider.ConnectionTimeout = TimeSpan.FromSeconds(2);
        return CorsairDeviceProvider.Instance;
    });

    /// <summary>Razer devices through Synapse (Chroma SDK, installed with Synapse).</summary>
    public static RgbNetBackend Razer() => new("razer", "Razer Synapse", _ => RazerDeviceProvider.Instance);

    // ---- provider lifetime --------------------------------------------------------------

    private AbstractRGBDeviceProvider Open(bool exclusive)
    {
        if (_provider is not null && _exclusive == exclusive && _provider.IsInitialized) return _provider;
        Close();
        var provider = _providerFor(exclusive);
        if (!ReferenceEquals(_subscribed, provider))
        {
            provider.Exception += OnProviderException;
            _subscribed = provider;
        }
        _errors.Reset();
        var console = Console.Out;
        try
        {
            _initializing = true;
            Console.SetOut(TextWriter.Null);   // the SDK wrapper prints debug lines
            if (!provider.Initialize(RGBDeviceType.All, throwExceptions: true))
                throw new BackendException($"{DisplayName} did not start");
        }
        catch
        {
            try { provider.Dispose(); } catch { /* ignore */ }
            throw;
        }
        finally
        {
            _initializing = false;
            Console.SetOut(console);
        }
        _provider = provider;
        _exclusive = exclusive;
        return provider;
    }

    private void Close()
    {
        try { _provider?.Dispose(); } catch { /* ignore */ }
        _provider = null;
    }

    /// <summary>
    /// A failed update (iCUE rejected a write) must not be rethrown once running: RGB.NET would
    /// rethrow it on its update thread, which ends that thread for good and freezes the lights
    /// with no error anywhere. Skip the frame instead (the next one resends every LED) and let
    /// Write reconnect if the failures keep coming.
    /// </summary>
    private void OnProviderException(object? sender, ExceptionEventArgs e)
    {
        if (_initializing) return;   // startup failures still surface as exceptions
        e.Throw = false;
        if (_errors.Add(Clock.Now, e.Exception.GetBaseException().Message))
            Log.Warn($"{DisplayName}: update failed: {_errors.LastMessage}");
    }

    private static T WithTimeout<T>(Func<T> work, TimeSpan timeout, string name)
    {
        var task = Task.Run(work);
        if (!task.Wait(timeout))
            throw new BackendException($"{name} is not responding (waited {timeout.TotalSeconds:0} s)");
        return task.Result;
    }

    public BackendStatus Probe(TimeSpan timeout)
    {
        try
        {
            int n = Discover(timeout).Count;
            return new BackendStatus(true, $"{DisplayName}: {n} device(s)");
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
                lock (_gate)
                {
                    var provider = Open(exclusive: _acquired > 0 && _exclusive);
                    var infos = Describe(provider.Devices);
                    if (_acquired == 0) Close();   // let the vendor app keep its lighting until racing starts
                    return infos;
                }
            }, timeout, DisplayName);
        }
        catch (Exception ex) when (ex is not BackendException)
        {
            if (ex is AggregateException agg && agg.Flatten().InnerException is BackendException b) throw b;
            throw new BackendException(Explain(ex), ex);
        }
    }

    private IReadOnlyList<DeviceInfo> Describe(IReadOnlyList<IRGBDevice> devices)
    {
        var locations = Locations(devices);
        var seen = new Dictionary<string, int>();
        var output = new List<DeviceInfo>();
        for (int position = 0; position < devices.Count; position++)
        {
            var d = devices[position];
            var leds = d.OrderBy(l => (int)l.Id).ToList();
            var name = d.DeviceInfo.DeviceName;
            int occurrence = seen.GetValueOrDefault(name);
            seen[name] = occurrence + 1;
            output.Add(new DeviceInfo
            {
                Backend = Id,
                Name = name,
                Occurrence = occurrence,
                Position = position,
                Kind = KindOf(d.DeviceInfo.DeviceType),
                Location = locations[position],
                LedCount = leds.Count,
                Zones = [new LedZone("LEDs", 0, Enumerable.Range(0, leds.Count).ToArray())],
                OriginalColors = [.. leds.Select(l => new Rgb(l.Color.GetR(), l.Color.GetG(), l.Color.GetB()))],
                OriginalMode = null,
                ModeNames = [],
            });
        }
        return output;
    }

    /// <summary>
    /// A stable location per device: route, hardware id, kind of LED, and which one of that
    /// kind it is on the controller (fans on one Commander Core all start at "Fan1").
    /// </summary>
    private List<string> Locations(IReadOnlyList<IRGBDevice> devices)
    {
        var counts = new Dictionary<string, int>();
        var output = new List<string>(devices.Count);
        foreach (var d in devices)
        {
            var id = d.DeviceInfo is CorsairRGBDeviceInfo c ? c.DeviceId : d.DeviceInfo.Model;
            var first = d.OrderBy(l => (int)l.Id).Select(l => l.Id.ToString()).FirstOrDefault() ?? "";
            var key = $"{Id}:{id}:{first}";
            int n = counts.GetValueOrDefault(key);
            counts[key] = n + 1;
            output.Add(n == 0 ? key : $"{key}#{n + 1}");
        }
        return output;
    }

    private static DeviceKind KindOf(RGBDeviceType t)
    {
        if (t.HasFlag(RGBDeviceType.Fan)) return DeviceKind.Fan;
        if (t.HasFlag(RGBDeviceType.Cooler)) return DeviceKind.Cooler;
        if (t.HasFlag(RGBDeviceType.DRAM)) return DeviceKind.Dram;
        if (t.HasFlag(RGBDeviceType.GraphicsCard)) return DeviceKind.Gpu;
        if (t.HasFlag(RGBDeviceType.Mainboard)) return DeviceKind.Motherboard;
        if (t.HasFlag(RGBDeviceType.Mouse)) return DeviceKind.Mouse;
        if (t.HasFlag(RGBDeviceType.Keyboard)) return DeviceKind.Keyboard;
        if (t.HasFlag(RGBDeviceType.Mousepad)) return DeviceKind.Mousemat;
        if (t.HasFlag(RGBDeviceType.Headset)) return DeviceKind.Headset;
        if (t.HasFlag(RGBDeviceType.HeadsetStand)) return DeviceKind.HeadsetStand;
        if (t.HasFlag(RGBDeviceType.LedStripe)) return DeviceKind.LedStrip;
        if (t.HasFlag(RGBDeviceType.Keypad)) return DeviceKind.Keypad;
        if (t.HasFlag(RGBDeviceType.Speaker)) return DeviceKind.Speaker;
        if (t.HasFlag(RGBDeviceType.GameController)) return DeviceKind.Gamepad;
        return DeviceKind.Unknown;
    }

    internal static string Explain(Exception ex)
    {
        var inner = ex is AggregateException agg ? agg.Flatten().InnerException ?? ex : ex;
        var text = inner.Message;
        if (inner is DllNotFoundException || text.Contains("dll", StringComparison.OrdinalIgnoreCase))
            return $"the vendor SDK isn't available ({text})";
        return text;
    }

    // ---- sessions -------------------------------------------------------------------------

    public IDeviceSession OpenSession(DeviceInfo device) => new Session(this, device);

    /// <summary>Find a discovered device in the currently open provider.</summary>
    private IRGBDevice Find(DeviceInfo info)
    {
        var provider = _provider ?? throw new BackendException($"{DisplayName} is not connected");
        var devices = provider.Devices;
        int at = Locations(devices).IndexOf(info.Location);
        var matches = devices.Where(d => d.DeviceInfo.DeviceName == info.Name).ToList();
        var device = at >= 0 ? devices[at] : (info.Occurrence < matches.Count ? matches[info.Occurrence] : null);
        if (device is null) throw new BackendException($"{info.Label} is no longer listed by {DisplayName}");
        return device;
    }

    private void Acquire()
    {
        lock (_gate)
        {
            if (_acquired == 0 || _provider is null) Open(exclusive: true);
            _acquired++;
            if (Id == "icue" && _logTimer is null)
            {
                _logWatcher = new ICueLogWatcher();
                _logWatcher.Poll();   // start at the end of iCUE's current log
                _logTimer = new Timer(_ => CheckICueLog(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            }
        }
    }

    /// <summary>
    /// When the hub drops a lighting write, iCUE's lighting for it can stall for good while still
    /// taking our colors. Drop the SDK session so the device workers reconnect (a short blink)
    /// and iCUE starts a fresh lighting stream.
    /// </summary>
    private void CheckICueLog()
    {
        int dropped;
        try { dropped = _logWatcher?.Poll() ?? 0; } catch { return; }
        if (dropped == 0) return;
        lock (_gate)
        {
            double now = Clock.Now;
            if (_acquired == 0 || _provider is null || now - _lastLogReconnect < LogReconnectGapSeconds) return;
            _lastLogReconnect = now;
            Log.Warn($"{DisplayName}: the Corsair hub ignored {dropped} lighting write(s) (iCUE log); reconnecting so iCUE's lighting doesn't stall");
            Close();
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (_acquired == 0) return;
            _acquired--;
            if (_acquired == 0) Close();   // the vendor app's own lighting comes back
        }
    }

    private void Write(DeviceInfo info, IReadOnlyList<Rgb> frame)
    {
        lock (_gate)
        {
            if (_errors.TooMany(Clock.Now))
            {
                // iCUE keeps refusing (lost control, restarted, hub busy): start a fresh SDK
                // session. Every device using it fails over and the workers reconnect shortly.
                var reason = _errors.LastMessage;
                Log.Warn($"{DisplayName} keeps rejecting updates ({reason}); reconnecting");
                Close();
                _errors.Reset();
                throw new BackendException($"{DisplayName} stopped accepting colors ({reason}); reconnecting");
            }
            var device = Find(info);
            var leds = device.OrderBy(l => (int)l.Id).ToList();
            if (leds.Count != frame.Count)
                throw new BackendException($"{info.Label}: frame has {frame.Count} colors for {leds.Count} LEDs");
            for (int i = 0; i < leds.Count; i++)
                leds[i].Color = new Color(frame[i].R, frame[i].G, frame[i].B);
            device.Update(flushLeds: true);
        }
    }

    /// <summary>Recent update failures; too many inside the window means the session is broken.</summary>
    public sealed class UpdateErrors(int limit = 5, double windowSeconds = 3.0)
    {
        private readonly Queue<double> _times = new();
        private readonly object _lock = new();

        public string? LastMessage { get; private set; }

        /// <summary>Record a failure; true when it starts a new burst (worth logging).</summary>
        public bool Add(double now, string message)
        {
            lock (_lock)
            {
                Trim(now);
                _times.Enqueue(now);
                LastMessage = message;
                return _times.Count == 1;
            }
        }

        public bool TooMany(double now)
        {
            lock (_lock)
            {
                Trim(now);
                return _times.Count >= limit;
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _times.Clear();
                LastMessage = null;
            }
        }

        private void Trim(double now)
        {
            while (_times.Count > 0 && now - _times.Peek() > windowSeconds) _times.Dequeue();
        }
    }

    private sealed class Session(RgbNetBackend backend, DeviceInfo info) : IDeviceSession
    {
        private bool _acquired;

        public void Connect() { }

        public VerifyResult Verify(bool engaged) => VerifyResult.Ok;

        public void Acquire()
        {
            if (_acquired) return;
            backend.Acquire();
            _acquired = true;
        }

        public void Write(IReadOnlyList<Rgb> frame)
        {
            if (!_acquired) Acquire();
            backend.Write(info, frame);
        }

        public void Release()
        {
            if (!_acquired) return;
            _acquired = false;
            backend.Release();
        }

        /// <summary>The vendor app restores its own lighting when PitGlow lets go.</summary>
        public void RestoreTo(string? modeName, IReadOnlyList<Rgb> colors) => Release();

        public void Dispose() => Release();
    }
}
