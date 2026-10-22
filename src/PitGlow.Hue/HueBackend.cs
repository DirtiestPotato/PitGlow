using PitGlow.Backends;
using PitGlow.Config;
using PitGlow.Devices;
using PitGlow.Engine;

namespace PitGlow.Hue;

/// <summary>
/// Philips Hue through the Hue Bridge. Each Entertainment area (set up in the Hue app) is one
/// device; each light in it, or each segment of a gradient light, is one LED. While PitGlow
/// drives an area the bridge streams our colors; when we stop, the lights go back to how they were.
/// </summary>
public sealed class HueBackend(HueCfg cfg, Func<HueBridge>? bridgeFactory = null) : IBackend
{
    public const string BackendId = "hue";

    private readonly Func<HueBridge> _bridge = bridgeFactory ?? (() => new HueBridge(cfg.Bridge, cfg.AppKey));
    private string? _appId;

    public string Id => BackendId;
    public string DisplayName => "Philips Hue";
    public HueCfg Cfg => cfg;

    public BackendStatus Probe(TimeSpan timeout)
    {
        try
        {
            using var b = _bridge();
            var info = b.Describe();
            return new BackendStatus(true, $"{info.Name} at {cfg.Bridge}");
        }
        catch (BackendException ex)
        {
            return new BackendStatus(false, ex.Message);
        }
    }

    public IReadOnlyList<DeviceInfo> Discover(TimeSpan timeout)
    {
        if (!cfg.Paired) throw new BackendException("not paired yet; run HueSetup.bat");
        using var b = _bridge();
        _appId = b.ApplicationId();
        var areas = b.Areas();
        if (areas.Count == 0)
            throw new BackendException("no Entertainment areas: in the Hue app, open Settings > Entertainment areas and create one with the lights you want");
        var colors = SafeColors(b);
        var seen = new Dictionary<string, int>();
        var list = new List<DeviceInfo>();
        foreach (var (area, position) in areas.Select((a, i) => (a, i)))
        {
            var name = $"Hue {area.Name}";
            int occurrence = seen.GetValueOrDefault(name);
            seen[name] = occurrence + 1;
            list.Add(new DeviceInfo
            {
                Backend = BackendId,
                Name = name,
                Occurrence = occurrence,
                Position = position,
                Kind = DeviceKind.Light,
                Location = $"hue:{area.Id}",
                LedCount = area.Channels.Count,
                Zones = [.. area.Channels.Select((c, i) => new LedZone(c.Name, i, [i]))],
                OriginalColors = [.. area.Channels.Select(c => c.LightId is string l && colors.TryGetValue(l, out var rgb) ? rgb : Rgb.Off)],
                OriginalMode = "hue",
                BackendState = area,
            });
        }
        return list;
    }

    private static IReadOnlyDictionary<string, Rgb> SafeColors(HueBridge b)
    {
        try { return b.LightColors(); }
        catch (BackendException) { return new Dictionary<string, Rgb>(); }
    }

    public IDeviceSession OpenSession(DeviceInfo device) => new HueSession(this, device);

    internal HueBridge Bridge() => _bridge();

    internal string AppId(HueBridge b) => _appId ??= b.ApplicationId();

    /// <summary>Message shown when iCUE, Hue Sync or another app is already streaming to the area.</summary>
    public static string InUseMessage(string area) =>
        $"Hue area \"{area}\" is being used by another app (iCUE's Philips Hue integration, Hue Sync or Razer Chroma). " +
        "Turn that off, or give PitGlow its own Entertainment area in the Hue app.";
}

internal sealed class HueSession(HueBackend backend, DeviceInfo info) : IDeviceSession
{
    private readonly string _areaId = ((HueArea)info.BackendState!).Id;
    private HueBridge? _bridge;
    private HueArea? _area;
    private string? _appId;
    private HueStream? _stream;
    private bool _started;

    public void Connect()
    {
        _bridge = backend.Bridge();
        _appId = backend.AppId(_bridge);
        _area = _bridge.Area(_areaId) ?? throw new BackendException($"{info.Label}: the Entertainment area was deleted in the Hue app");
        if (_area.Channels.Count != info.LedCount)
            throw new BackendException($"{info.Label}: lights were added or removed in the Hue app; restart PitGlow");
    }

    public VerifyResult Verify(bool engaged)
    {
        var area = _bridge!.Area(_areaId) ?? throw new BackendException($"{info.Label}: the Entertainment area was deleted in the Hue app");
        if (!engaged) return VerifyResult.Ok;
        if (area.InUseByOther(_appId)) return VerifyResult.ChangedByOther;
        if (area.Status != "active" || _stream?.Failure is not null)
        {
            StopStream();
            return VerifyResult.Moved;   // the bridge dropped our stream: take it again
        }
        return VerifyResult.Ok;
    }

    public void Acquire()
    {
        var area = _bridge!.Area(_areaId) ?? throw new BackendException($"{info.Label}: the Entertainment area was deleted in the Hue app");
        if (area.InUseByOther(_appId)) throw new BackendException(HueBackend.InUseMessage(area.Name));
        _bridge.Start(_areaId);
        _started = true;
        _stream = HueStream.Open(_bridge.Ip, _appId!, backend.Cfg.ClientKey, _areaId, area.Channels);
    }

    public void Write(IReadOnlyList<Rgb> frame)
    {
        if (_stream is null) throw new BackendException($"{info.Label}: not streaming");
        _stream.Send(frame);
    }

    /// <summary>Stopping the stream hands the lights back; the bridge restores their previous state.</summary>
    public void Release() => StopStream();

    public void RestoreTo(string? modeName, IReadOnlyList<Rgb> colors)
    {
        _bridge ??= backend.Bridge();
        _bridge.Stop(_areaId);
    }

    private void StopStream()
    {
        _stream?.Dispose();
        _stream = null;
        if (_started)
        {
            _started = false;
            try { _bridge?.Stop(_areaId); }
            catch (BackendException ex) { Log.Warn($"{info.Label}: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        StopStream();
        _bridge?.Dispose();
        _bridge = null;
    }
}
