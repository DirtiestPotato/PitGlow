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
    {
        _cfg = cfg;
        _source = source;
        _layouts = layouts;
        _renderer = new Renderer(cfg);
        foreach (var layout in layouts)
        {
            var info = layout.Info;
            _workers[layout] = new DeviceWorker(info, () => backend.OpenSession(info), layout.Cfg.MaxFps, journal);
        }
    }

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
                        foreach (var w in _workers.Values) w.Submit(null);
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
