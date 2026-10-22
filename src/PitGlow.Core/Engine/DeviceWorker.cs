using PitGlow.Backends;
using PitGlow.Devices;

namespace PitGlow.Engine;

/// <summary>
/// Owns one device: takes control, streams frames, restores it. Each device gets its own
/// thread and connection, so a slow device (RAM over SMBus) never delays the fans, and a
/// device that hangs never blocks the others.
/// </summary>
public sealed class DeviceWorker
{
    public static double KeepaliveSeconds = 1.0;   // resend an unchanged frame this often (lost packets)
    public static double RecheckSeconds = 10.0;    // re-verify the device is still there
    public static double RetrySeconds = 2.0;
    public static double ParkedRetrySeconds = 30.0;
    public const int FailuresBeforePark = 3;

    private readonly Func<IDeviceSession> _sessionFactory;
    private readonly double _minInterval;
    private readonly RestoreJournal? _journal;
    private readonly object _gate = new();
    private readonly Thread _thread;

    private Rgb[]? _want;
    private int _version;
    private bool _stopping;
    private IDeviceSession? _session;
    private bool _engaged;
    private bool _backedOff;
    private Rgb[]? _lastSent;
    private double _lastSendTime = double.NegativeInfinity;
    private double _lastCheck;
    private int _failures;

    public DeviceWorker(DeviceInfo info, Func<IDeviceSession> sessionFactory, double maxFps, RestoreJournal? journal = null)
    {
        Info = info;
        _sessionFactory = sessionFactory;
        _minInterval = 1.0 / Math.Max(1.0, maxFps);
        _journal = journal;
        _thread = new Thread(Run) { IsBackground = true, Name = $"rgb:{info.Label}" };
    }

    public DeviceInfo Info { get; }
    public string? Error { get; private set; }
    public int SentFrames { get; private set; }
    public bool IsAlive => _thread.IsAlive;

    public void Start() => _thread.Start();

    /// <summary>Queue a frame, or null to hand the device back (player left the car).</summary>
    public void Submit(IReadOnlyList<Rgb>? colors)
    {
        lock (_gate)
        {
            if (Same(colors, _want)) return;
            _want = colors?.ToArray();
            _version++;
            Monitor.PulseAll(_gate);
        }
    }

    public void Stop(double timeoutSeconds = 5.0)
    {
        lock (_gate)
        {
            _stopping = true;
            Monitor.PulseAll(_gate);
        }
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(timeoutSeconds));
    }

    private static bool Same(IReadOnlyList<Rgb>? a, IReadOnlyList<Rgb>? b) =>
        ReferenceEquals(a, b) || (a is not null && b is not null && a.SequenceEqual(b));

    private void Run()
    {
        double retryAt = 0.0;
        while (true)
        {
            Rgb[]? want;
            lock (_gate)
            {
                int seen = _version;
                double timeout = _engaged ? KeepaliveSeconds : RetrySeconds;
                var until = Clock.Now + timeout;
                while (!_stopping && _version == seen)
                {
                    var left = until - Clock.Now;
                    if (left <= 0) break;
                    Monitor.Wait(_gate, TimeSpan.FromSeconds(left));
                }
                if (_stopping) break;
            }
            double now = Clock.Now;
            try
            {
                if (_session is null)
                {
                    if (now < retryAt) continue;
                    Connect();
                }
                else if (now - _lastCheck > RecheckSeconds)
                {
                    var result = _session.Verify(_engaged);
                    _lastCheck = Clock.Now;
                    if (result == VerifyResult.Moved) _engaged = false;
                    else if (result == VerifyResult.ChangedByOther && _engaged)
                    {
                        // Someone else (iCUE, SignalRGB, the user) took the device: back off.
                        _engaged = false;
                        _backedOff = true;
                        _journal?.Remove(Info);
                        Error = $"{Info.Label}: another app changed its lighting; PitGlow backed off until the next session";
                        Log.Warn(Error);
                    }
                }
                double wait = _lastSendTime + _minInterval - Clock.Now;
                if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds(wait));
                lock (_gate) want = _want;
                if (want is null)
                {
                    _backedOff = false;   // the next driving session may take the device again
                    if (_engaged)
                    {
                        _session!.Release();
                        _engaged = false;
                        _lastSent = null;
                        _journal?.Remove(Info);
                    }
                    continue;
                }
                if (_backedOff) continue;
                if (!_engaged)
                {
                    _journal?.Add(Info);
                    _session!.Acquire();
                    _engaged = true;
                    _lastSent = null;
                }
                if (!Same(want, _lastSent) || Clock.Now - _lastSendTime > KeepaliveSeconds)
                {
                    _session!.Write(want);
                    _lastSent = want;
                    _lastSendTime = Clock.Now;
                    SentFrames++;
                }
                _failures = 0;
                if (!_backedOff) Error = null;
            }
            catch (Exception ex)
            {
                _failures++;
                double delay = _failures >= FailuresBeforePark ? ParkedRetrySeconds : RetrySeconds;
                Error = $"{Info.Label}: {ex.Message}";
                Log.Warn($"{Error} (retrying in {delay:0}s)");
                Disconnect();
                retryAt = Clock.Now + delay;
            }
        }
        try
        {
            if (_session is not null && _engaged)
            {
                _session.Release();
                _journal?.Remove(Info);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"{Info.Label}: could not restore lighting: {ex.Message}");
        }
        finally
        {
            Disconnect();
        }
    }

    private void Connect()
    {
        var session = _sessionFactory();
        try
        {
            session.Connect();
        }
        catch
        {
            session.Dispose();
            throw;
        }
        _session = session;
        _lastCheck = Clock.Now;
    }

    private void Disconnect()
    {
        try { _session?.Dispose(); } catch { /* ignore */ }
        _session = null;
        _engaged = false;
        _lastSent = null;
    }
}
