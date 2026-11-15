using System.Diagnostics;
using PitGlow.Backends;
using PitGlow.Devices;
using PitGlow.Engine;

namespace PitGlow.CorsairHub;

/// <summary>
/// Corsair Commander Core hubs driven directly, without iCUE or OpenRGB. The pump ring and each
/// fan port show up as their own device. PitGlow only sends colors: the hub keeps running the
/// fan and pump curves saved in its memory (iCUE's Device Memory Mode) the whole time, and gets
/// its own lighting back when PitGlow lets go.
/// </summary>
public sealed class CorsairHubBackend(
    Func<string, IHubTransport>? open = null,
    Func<IReadOnlyList<string>>? find = null,
    Func<bool>? icueRunning = null) : IBackend
{
    public const string BackendId = "corsairhub";

    private readonly Func<string, IHubTransport> _open = open ?? (p => new HidTransport(p));
    private readonly Func<IReadOnlyList<string>> _find = find ?? HidTransport.Find;
    private readonly Func<bool> _icueRunning = icueRunning ?? (() => Process.GetProcessesByName("iCUE").Length > 0);
    private readonly Dictionary<string, HubLink> _links = [];
    private readonly object _gate = new();

    public string Id => BackendId;
    public string DisplayName => "Corsair hub (direct)";

    /// <summary>Set when a safety check tripped: PitGlow leaves the hub alone until it restarts.</summary>
    public string? SafetyStop { get; internal set; }

    public BackendStatus Probe(TimeSpan timeout)
    {
        var hubs = _find();
        return new BackendStatus(hubs.Count > 0, hubs.Count > 0 ? $"{hubs.Count} Commander Core hub(s)" : "no Commander Core hub found");
    }

    public IReadOnlyList<DeviceInfo> Discover(TimeSpan timeout)
    {
        var paths = _find();
        if (paths.Count == 0) return [];
        if (_icueRunning())
            throw new BackendException("iCUE is running, and it talks to the same Corsair hub. Close iCUE (and stop it starting with Windows) " +
                                       "so PitGlow can drive the hub, or set enabled = false under [corsair_hub] to use iCUE instead");
        var list = new List<DeviceInfo>();
        foreach (var (path, hub) in paths.Select((p, i) => (p, i)))
        {
            int[] counts;
            using (var core = new CommanderCore(_open(path)))
            {
                var firmware = core.Firmware();
                core.Wake();
                try { counts = core.LedCounts(); }
                finally { core.Sleep(); }
                Log.Info($"Corsair hub {hub + 1}: firmware {firmware}, LEDs per channel [{string.Join(", ", counts)}]");
            }
            string suffix = hub == 0 ? "" : $" (hub {hub + 1})";
            for (int ch = 0; ch < counts.Length; ch++)
            {
                if (counts[ch] <= 0) continue;
                int leds = Math.Min(counts[ch], ch == 0 ? counts[0] : CommanderCore.PortSlots);
                list.Add(new DeviceInfo
                {
                    Backend = BackendId,
                    Name = (ch == 0 ? "Corsair Pump" : $"Corsair Fan {ch}") + suffix,
                    Position = list.Count,
                    Kind = ch == 0 ? DeviceKind.Cooler : DeviceKind.Fan,
                    Location = $"corsairhub:{path}#{(ch == 0 ? "pump" : $"fan{ch}")}",
                    LedCount = leds,
                    Zones = [new LedZone("LEDs", 0, [.. Enumerable.Range(0, leds)])],
                    OriginalColors = [.. Enumerable.Repeat(Rgb.Off, leds)],
                    OriginalMode = "hardware",
                    BackendState = new HubChannel(path, ch, counts[0]),
                });
            }
        }
        return list;
    }

    public IDeviceSession OpenSession(DeviceInfo device) => new HubSession(this, device, (HubChannel)device.BackendState!);

    internal HubLink Link(HubChannel ch)
    {
        lock (_gate)
        {
            if (!_links.TryGetValue(ch.Path, out var link) || link.Failure is not null)
            {
                link?.Dispose();
                link = new HubLink(this, new CommanderCore(_open(ch.Path)), ch.PumpLeds, () => { lock (_gate) _links.Remove(ch.Path); });
                _links[ch.Path] = link;
            }
            link.AddRef();
            return link;
        }
    }

    /// <summary>Hand a hub back to its own lighting (crash recovery).</summary>
    internal void HandBack(string path)
    {
        using var core = new CommanderCore(_open(path));
        core.Sleep();
    }
}

internal sealed record HubChannel(string Path, int Channel, int PumpLeds);

/// <summary>
/// One hub, shared by the sessions of its pump and fans. A single thread does all the hub's
/// I/O: it merges every channel's colors into one buffer, sends it (at most 30 times a second,
/// and at least once a second so the hub stays in software mode), and watches the pump and
/// coolant while PitGlow is in control.
/// </summary>
internal sealed class HubLink : IDisposable
{
    public static double MinFrameSeconds = 1.0 / 30;
    public static double KeepaliveSeconds = 1.0;
    public static double SensorSeconds = 2.0;
    public static double SensorLogSeconds = 60.0;
    public const int MinPumpRpm = 500;
    public const double MaxCoolantC = 55.0;
    public const int TripReads = 3;

    private readonly CorsairHubBackend _backend;
    private readonly CommanderCore _core;
    private readonly int _pumpLeds;
    private readonly Action _onClosed;
    private readonly object _gate = new();
    private readonly Rgb[]?[] _colors = new Rgb[]?[1 + CommanderCore.Ports];
    private readonly bool[] _engaged = new bool[1 + CommanderCore.Ports];
    private readonly Thread _thread;
    private int _refs;
    private bool _dirty;
    private bool _stopping;
    private bool _awake;

    public HubLink(CorsairHubBackend backend, CommanderCore core, int pumpLeds, Action onClosed)
    {
        _backend = backend;
        _core = core;
        _pumpLeds = pumpLeds;
        _onClosed = onClosed;
        _thread = new Thread(Run) { IsBackground = true, Name = "corsair-hub" };
        _thread.Start();
    }

    public string? Failure { get; private set; }
    public HubSensors? LastSensors { get; private set; }
    public bool Awake { get { lock (_gate) return _awake; } }

    public void AddRef() { lock (_gate) _refs++; }

    public void Unref()
    {
        bool last;
        lock (_gate) last = --_refs <= 0;
        if (last) Dispose();
    }

    public void Engage(int ch) => Change(() => _engaged[ch] = true);

    public void Disengage(int ch) => Change(() => { _engaged[ch] = false; _colors[ch] = null; });

    public void SetColors(int ch, IReadOnlyList<Rgb> frame)
    {
        if (Failure is not null) throw new BackendException(Failure);
        Change(() => _colors[ch] = [.. frame]);
    }

    private void Change(Action change)
    {
        lock (_gate)
        {
            change();
            _dirty = true;
            Monitor.PulseAll(_gate);
        }
    }

    private void Run()
    {
        double lastSend = double.NegativeInfinity, lastSensors = double.NegativeInfinity, lastLog = double.NegativeInfinity;
        int bad = 0;
        try
        {
            while (true)
            {
                bool engaged, dirty;
                Rgb[]?[] colors;
                lock (_gate)
                {
                    if (!_stopping && !_dirty) Monitor.Wait(_gate, TimeSpan.FromSeconds(MinFrameSeconds));
                    if (_stopping) break;
                    engaged = _engaged.Any(e => e);
                    dirty = _dirty;
                    _dirty = false;
                    colors = (Rgb[]?[])_colors.Clone();
                }
                double now = Clock.Now;
                if (engaged && !_awake)
                {
                    _core.Wake();
                    lock (_gate) _awake = true;
                    lastSensors = lastLog = double.NegativeInfinity;
                    Log.Info("Corsair hub: PitGlow drives the lights (fan and pump curves stay on the hub)");
                }
                else if (!engaged && _awake)
                {
                    _core.Sleep();
                    lock (_gate) _awake = false;
                    Log.Info("Corsair hub: lights handed back to the hub");
                }
                if (!_awake) continue;

                if (dirty || now - lastSend >= KeepaliveSeconds)
                {
                    _core.SendColors(CommanderCore.Layout(_pumpLeds, colors));
                    lastSend = now;
                    double wait = lastSend + MinFrameSeconds - Clock.Now;
                    if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds(wait));
                }
                if (now - lastSensors >= SensorSeconds)
                {
                    var s = LastSensors = _core.Sensors();
                    lastSensors = now;
                    if (now - lastLog >= SensorLogSeconds)
                    {
                        Log.Info($"Corsair hub: pump {s.PumpRpm} rpm, fans {string.Join(" ", s.FanRpm)} rpm, coolant {s.CoolantC?.ToString("0.0") ?? "?"} C");
                        lastLog = now;
                    }
                    bool trip = s.PumpRpm < MinPumpRpm || s.CoolantC >= MaxCoolantC;
                    bad = trip ? bad + 1 : 0;
                    if (bad >= TripReads)
                    {
                        _backend.SafetyStop = $"Corsair hub safety stop: pump {s.PumpRpm} rpm, coolant {s.CoolantC?.ToString("0.0") ?? "?"} C. " +
                                              "PitGlow handed the hub back and leaves it alone until it restarts; check the cooler";
                        Log.Error(_backend.SafetyStop);
                        Failure = _backend.SafetyStop;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Failure = $"Corsair hub: {ex.Message}";
            Log.Warn(Failure);
        }
        if (_awake)
        {
            try
            {
                _core.Sleep();
                Log.Info("Corsair hub: lights handed back to the hub");
            }
            catch { /* the hub returns to its own mode by itself */ }
            lock (_gate) _awake = false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true;
            Monitor.PulseAll(_gate);
        }
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(3));
        _core.Dispose();
        _onClosed();
    }
}

internal sealed class HubSession(CorsairHubBackend backend, DeviceInfo info, HubChannel ch) : IDeviceSession
{
    private HubLink? _link;

    public void Connect() => _link = backend.Link(ch);

    public VerifyResult Verify(bool engaged) =>
        _link?.Failure is string f ? throw new BackendException(f) : VerifyResult.Ok;

    public void Acquire()
    {
        if (backend.SafetyStop is string stop) throw new BackendException(stop);
        Link().Engage(ch.Channel);
    }

    public void Write(IReadOnlyList<Rgb> frame) => Link().SetColors(ch.Channel, frame);

    public void Release() => _link?.Disengage(ch.Channel);

    public void RestoreTo(string? modeName, IReadOnlyList<Rgb> colors)
    {
        if (_link is null) backend.HandBack(ch.Path);
        else _link.Disengage(ch.Channel);
    }

    private HubLink Link() => _link ?? throw new BackendException($"{info.Label}: not connected");

    public void Dispose()
    {
        _link?.Disengage(ch.Channel);
        _link?.Unref();
        _link = null;
    }
}
