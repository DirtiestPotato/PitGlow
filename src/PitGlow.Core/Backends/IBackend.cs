using PitGlow.Devices;

namespace PitGlow.Backends;

public sealed record BackendStatus(bool Available, string Detail);

/// <summary>
/// A route to lighting hardware (OpenRGB, a vendor SDK, Windows Dynamic Lighting).
/// Everything above this interface never learns which brand of hardware it is lighting.
/// </summary>
public interface IBackend
{
    /// <summary>Short stable id, e.g. "openrgb".</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Is this backend usable here, and which version. Must return within the timeout.</summary>
    BackendStatus Probe(TimeSpan timeout);

    /// <summary>List devices in the shared device shape. Throws BackendException on failure or timeout.</summary>
    IReadOnlyList<DeviceInfo> Discover(TimeSpan timeout);

    /// <summary>A private connection for driving one device. Each device worker owns one.</summary>
    IDeviceSession OpenSession(DeviceInfo device);
}

public enum VerifyResult { Ok, Moved, ChangedByOther }

/// <summary>One device, driven by one worker thread.</summary>
public interface IDeviceSession : IDisposable
{
    /// <summary>Connect and locate the device. Throws if it is gone or its LED count changed.</summary>
    void Connect();

    /// <summary>Periodic re-check while connected. Throws if the device disappeared.</summary>
    VerifyResult Verify(bool engaged);

    /// <summary>Take control (switch to per-LED / direct mode).</summary>
    void Acquire();

    /// <summary>Send one complete frame, one color per LED of the whole device.</summary>
    void Write(IReadOnlyList<Rgb> frame);

    /// <summary>Restore the lighting captured at discovery and hand control back.</summary>
    void Release();

    /// <summary>Restore a specific mode and colors (crash recovery from the restore file).</summary>
    void RestoreTo(string? modeName, IReadOnlyList<Rgb> colors);
}

/// <summary>A backend failure with a message meant for the user.</summary>
public sealed class BackendException(string message, Exception? inner = null) : Exception(message, inner);
