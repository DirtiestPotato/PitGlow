using PitGlow.Backends;
using PitGlow.Config;
using PitGlow.Devices;
using PitGlow.Rendering;
using PitGlow.Sources;

namespace PitGlow.Engine;

public sealed record RunStatus(string Text);

/// <summary>
/// The racing loop: read the game ~60 times a second, render one frame per device, hand
/// frames to the device workers. Restores everything when the player leaves the car, when
/// the loop stalls (watchdog) and on exit.
/// </summary>
public sealed class Runner
{
    public const int Fps = 60;
    public const double IdleRestoreSeconds = 2.0;
    public const double WatchdogSeconds = 2.0;

    private readonly Settings _cfg;
    private readonly ITelemetrySource _source;
    private readonly IReadOnlyList<Layout> _layouts;
    private readonly Dictionary<Layout, DeviceWorker> _workers = [];
    private readonly Renderer _renderer;
    private double _heartbeat;

    public Runner(Settings cfg, ITelemetrySource source, IBackend backend, IReadOnlyList<Layout> layouts, RestoreJournal? journal)
        : this(cfg, source, backend.OpenSession, layouts, journal) { }

    public Runner(Settings cfg, ITelemetrySource source, Func<DeviceInfo, IDeviceSession> openSession,
                  IReadOnlyList<Layout> layouts, RestoreJournal? journal)
    {
        _cfg = cfg;
        _source = source;
        _layouts = layouts;
        _renderer = new Renderer(cfg);
        foreach (var layout in layouts)
        {
            var info = layout.Info;
            double fps = info.Backend == "icue" ? Math.Min(layout.Cfg.MaxFps, cfg.ICueMaxFps) : layout.Cfg.MaxFps;
            _workers[layout] = new DeviceWorker(info, () => openSession(info), fps, journal);
        }
    }

    private bool HoldsControl(Layout layout) => _cfg.ICueHoldControl && layout.Info.Backend == "icue";

    public IEnumerable<DeviceWorker> Workers => _workers.Values;
    public Renderer Renderer => _renderer;

    /// <summary>Run until stopped, or for the given number of seconds.</summary>
    public void Run(CancellationToken stop, double? seconds = null, Action<string>? status = null)
    {
        foreach (var w in _workers.Values) w.Start();
        double started = Clock.Now;
        bool driving = false;
        double? idleSince = null;
        double nextStatus = 0;
        double nextDiagnostics = 0;
        string? lastGame = null;
        Sample? sample = null;
        _heartbeat = Clock.Now;
        using var watchdogStop = new CancellationTokenSource();
        var watchdog = new Thread(() => Watchdog(watchdogStop.Token)) { IsBackground = true, Name = "watchdog" };
        watchdog.Start();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                double now = Clock.Now;
                _heartbeat = now;
                if (seconds is double s && now - started >= s) break;
                sample = _source.Read(now);
                if ((sample?.Game) != lastGame)
                {
                    Log.Info(sample is null ? $"No game (was {lastGame})" : $"Game: {sample.Game} (mode {_cfg.Mode})");
                    lastGame = sample?.Game;
                }
                if (driving && now >= nextDiagnostics)
                {
                    nextDiagnostics = now + 10;
                    Log.Info($"Devices at {sample?.Rpm ?? 0:0} rpm, flag {(_renderer.Flag is Flag f ? f.ConfigName() : "none")}: " +
                             string.Join(" | ", _workers.Values.Select(w => w.Diagnostics())));
                }
                if (sample is not null)
                {
                    _renderer.Update(sample, now);
                    foreach (var layout in _layouts)
                        _workers[layout].Submit(_renderer.Frame(layout, sample, now));
                    driving = true;
                    idleSince = null;
                }
                else if (driving)
                {
                    idleSince ??= now;
                    if (now - idleSince > IdleRestoreSeconds)
                    {
                        // Hand devices back between sessions, except iCUE ones when hold_control is on:
                        // iCUE taking over and giving back every time the game loses focus wears on it,
                        // so those keep PitGlow's idle look until PitGlow itself stops.
                        foreach (var layout in _layouts)
                            _workers[layout].Submit(HoldsControl(layout) ? _renderer.IdleFrame(layout) : null);
                        _renderer.Reset();
                        driving = false;
                    }
                }
                if (status is not null && now >= nextStatus)
                {
                    nextStatus = now + 0.5;
                    status(StatusLine(sample));
                }
                stop.WaitHandle.WaitOne(TimeSpan.FromSeconds(1.0 / Fps));
            }
        }
        finally
        {
            watchdogStop.Cancel();
            foreach (var w in _workers.Values) w.Stop();
            _source.Close();
        }
    }

    /// <summary>If the main loop stalls, hand every device back rather than freeze on one frame.</summary>
    private void Watchdog(CancellationToken stop)
    {
        bool tripped = false;
        while (!stop.WaitHandle.WaitOne(500))
        {
            bool stalled = Clock.Now - _heartbeat > WatchdogSeconds;
            if (stalled && !tripped)
            {
                Log.Warn("Main loop stalled; restoring lighting until it recovers");
                foreach (var w in _workers.Values) w.Submit(null);
                tripped = true;
            }
            else if (!stalled) tripped = false;
        }
    }

    private string StatusLine(Sample? sample)
    {
        string text;
        if (sample is null)
        {
            text = $"Waiting: {_source.Game}";
            if (_source is AutoSource auto && auto.Errors().Any()) text += " | " + string.Join("; ", auto.Errors());
        }
        else
        {
            var flag = _renderer.Flag is Flag f ? f.ConfigName() : "none";
            text = $"{sample.Game}: {sample.Rpm,6:N0} rpm | flag: {flag}";
            if (sample.PitLimiter) text += " | PIT LIMITER";
        }
        var errors = _workers.Values.Select(w => w.Error).OfType<string>().ToList();
        if (errors.Count > 0) text += " | " + string.Join("; ", errors);
        return text;
    }
}
