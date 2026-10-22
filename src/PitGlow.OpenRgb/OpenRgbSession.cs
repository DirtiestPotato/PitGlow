using PitGlow.Backends;
using PitGlow.Devices;

namespace PitGlow.OpenRgb;

/// <summary>One OpenRGB device, driven over its own SDK connection.</summary>
internal sealed class OpenRgbSession(OpenRgbBackend backend, DeviceInfo info) : IDeviceSession
{
    private OpenRgbClient? _client;
    private int _index = -1;
    private ControllerData? _current;

    private ControllerData Original => info.BackendState as ControllerData
        ?? throw new BackendException($"{info.Label}: no snapshot from discovery");

    public void Connect()
    {
        _client = backend.NewClient("Potato PitGlow");
        Locate();
    }

    /// <summary>Find our device in OpenRGB's current list (rescans can renumber devices).</summary>
    private bool Locate()
    {
        var all = _client!.AllControllers();
        int occurrence = 0, found = -1;
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].Name != info.Name) continue;
            if (occurrence == info.Occurrence) { found = i; break; }
            occurrence++;
        }
        if (found < 0 && info.Location.Length > 0)
            found = all.FindIndex(c => c.Location == info.Location);
        if (found < 0) throw new BackendException($"{info.Label} is no longer listed in OpenRGB");
        if (all[found].Leds.Count != info.LedCount)
            throw new BackendException($"{info.Label}: LED count changed ({info.LedCount} -> {all[found].Leds.Count}); run Init again");
        bool moved = _index >= 0 && _index != found;
        _index = found;
        _current = all[found];
        return moved;
    }

    public VerifyResult Verify(bool engaged)
    {
        var client = _client ?? throw new BackendException("not connected");
        ControllerData? data = null;
        if (!client.DeviceListChanged)
        {
            try { data = client.Controller(_index); }
            catch (InvalidDataException) { data = null; }
        }
        if (data is null || data.Name != info.Name || data.Leds.Count != info.LedCount)
            return Locate() ? VerifyResult.Moved : VerifyResult.Ok;
        _current = data;
        if (engaged && !IsDirect(data)) return VerifyResult.ChangedByOther;
        return VerifyResult.Ok;
    }

    private static bool IsDirect(ControllerData c) =>
        string.Equals(c.ActiveModeName, "direct", StringComparison.OrdinalIgnoreCase) ||
        (c.ActiveMode >= 0 && c.ActiveMode < c.Modes.Count && c.Modes[c.ActiveMode].ColorMode == ColorMode.PerLed &&
         c.FindMode("direct") < 0);

    public void Acquire()
    {
        var c = _current ?? Original;
        if (IsDirect(c)) return;
        int direct = c.FindMode("direct");
        if (direct >= 0)
            _client!.UpdateMode(_index, direct, c.Modes[direct]);
        else
            _client!.SetCustomMode(_index);
        _current = c with { ActiveMode = direct >= 0 ? direct : c.ActiveMode };
    }

    public void Write(IReadOnlyList<Rgb> frame)
    {
        if (frame.Count != info.LedCount)
            throw new BackendException($"{info.Label}: frame has {frame.Count} colors for {info.LedCount} LEDs");
        _client!.UpdateLeds(_index, frame);
    }

    public void Release()
    {
        var o = Original;
        RestoreTo(o.ActiveModeName, [.. o.Colors.Select(ControllerData.Unpack)], o);
    }

    public void RestoreTo(string? modeName, IReadOnlyList<Rgb> colors) => RestoreTo(modeName, colors, null);

    private void RestoreTo(string? modeName, IReadOnlyList<Rgb> colors, ControllerData? snapshot)
    {
        var client = _client ?? throw new BackendException("not connected");
        var now = _current ?? Original;
        int target = modeName is null ? -1 : (snapshot ?? now).FindMode(modeName);
        if (target >= 0 && !string.Equals(now.ActiveModeName, modeName, StringComparison.OrdinalIgnoreCase))
        {
            // Send the mode as it was at discovery, so its own colors and speed come back too.
            var mode = (snapshot ?? now).Modes[target];
            client.UpdateMode(_index, target, mode);
        }
        var modeData = target >= 0 ? (snapshot ?? now).Modes[target] : null;
        if ((modeData is null || modeData.ColorMode == ColorMode.PerLed) && colors.Count == info.LedCount)
            client.UpdateLeds(_index, colors);
        _current = now with { ActiveMode = target >= 0 ? target : now.ActiveMode };
    }

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
    }
}
