using PitGlow.Backends;
using PitGlow.Devices;
using PitGlow.Engine;
using PitGlow.Hue;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace PitGlow.HueBluetooth;

/// <summary>
/// Philips Hue Bluetooth bulbs, straight from the PC's Bluetooth (no Hue Bridge). Each bulb
/// paired with Windows is one device with one LED. Bluetooth takes roughly 10 color changes a
/// second per bulb, plenty for flags; the bulb goes back to how it was when PitGlow lets go.
/// </summary>
public sealed class HueBluetoothBackend : IBackend
{
    public const string BackendId = "huebt";
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    public string Id => BackendId;
    public string DisplayName => "Philips Hue (Bluetooth)";

    public BackendStatus Probe(TimeSpan timeout)
    {
        try
        {
            var adapter = Ble.Wait(BluetoothAdapter.GetDefaultAsync(), timeout, "find the Bluetooth adapter");
            return adapter is { IsLowEnergySupported: true }
                ? new BackendStatus(true, "Bluetooth LE ready")
                : new BackendStatus(false, "this PC has no Bluetooth LE adapter");
        }
        catch (Exception ex)
        {
            return new BackendStatus(false, ex.Message);
        }
    }

    public IReadOnlyList<DeviceInfo> Discover(TimeSpan timeout)
    {
        var adapter = Ble.Wait(BluetoothAdapter.GetDefaultAsync(), timeout, "find the Bluetooth adapter");
        if (adapter is null || !adapter.IsLowEnergySupported) return [];
        var list = new List<DeviceInfo>();
        var seen = new Dictionary<string, int>();
        foreach (var bulb in PairedBulbs(timeout))
        {
            int occurrence = seen.GetValueOrDefault(bulb.Name);
            seen[bulb.Name] = occurrence + 1;
            list.Add(new DeviceInfo
            {
                Backend = BackendId,
                Name = bulb.Name,
                Occurrence = occurrence,
                Position = list.Count,
                Kind = DeviceKind.Light,
                Location = Location(bulb.Address),
                LedCount = 1,
                Zones = [new LedZone("Light", 0, [0])],
                OriginalColors = [bulb.Look],
                OriginalMode = "hue",
                BackendState = bulb.Address,
            });
        }
        return list;
    }

    public static string Location(ulong address) => $"huebt:{address:X12}";

    public sealed record Bulb(ulong Address, string Name, Rgb Look);

    /// <summary>Hue bulbs already paired with Windows. Uses Windows' cached data, so bulbs out of range still show.</summary>
    public static IReadOnlyList<Bulb> PairedBulbs(TimeSpan timeout)
    {
        var selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
        var infos = Ble.Wait(DeviceInformation.FindAllAsync(selector), timeout, "list paired Bluetooth devices");
        var bulbs = new List<Bulb>();
        foreach (var info in infos)
        {
            BluetoothLEDevice? device = null;
            try
            {
                device = Ble.Wait(BluetoothLEDevice.FromIdAsync(info.Id), TimeSpan.FromSeconds(3), "open a paired device");
                if (device is null) continue;
                bool hue = HueBleProtocol.LooksLikeHue(info.Name);
                var services = Ble.Wait(device.GetGattServicesForUuidAsync(HueBleProtocol.LightService, BluetoothCacheMode.Cached),
                                        TimeSpan.FromSeconds(3), "check the device");
                hue |= services.Status == GattCommunicationStatus.Success && services.Services.Count > 0;
                if (!hue) continue;
                var look = new Rgb(255, 180, 110);   // until the bulb answers: warm white
                var service = services.Services.FirstOrDefault();
                if (service is not null)
                {
                    var state = ReadState(service, BluetoothCacheMode.Cached, TimeSpan.FromSeconds(2));
                    if (state is not null) look = state.Look;
                }
                foreach (var s in services.Services) s.Dispose();
                bulbs.Add(new Bulb(device.BluetoothAddress, string.IsNullOrWhiteSpace(info.Name) ? "Hue light" : info.Name.Trim(), look));
            }
            catch (BackendException)
            {
                // One unreachable device mustn't hide the others.
            }
            finally
            {
                device?.Dispose();
            }
        }
        return bulbs;
    }

    internal static HueBleProtocol.State? ReadState(GattDeviceService service, BluetoothCacheMode mode, TimeSpan timeout)
    {
        var chars = Ble.Wait(service.GetCharacteristicsForUuidAsync(HueBleProtocol.LightState, mode), timeout, "find the light control");
        var c = chars.Characteristics.FirstOrDefault();
        if (chars.Status != GattCommunicationStatus.Success || c is null) return null;
        var read = Ble.Wait(c.ReadValueAsync(mode), timeout, "read the light");
        if (read.Status != GattCommunicationStatus.Success) return null;
        using var reader = DataReader.FromBuffer(read.Value);
        var bytes = new byte[read.Value.Length];
        reader.ReadBytes(bytes);
        return HueBleProtocol.Parse(bytes);
    }

    public IDeviceSession OpenSession(DeviceInfo device) => new HueBluetoothSession(device);

    // ---- setup: find bulbs in pairing mode and pair them ------------------------------

    public sealed record Found(ulong Address, string Name, short Rssi);

    /// <summary>Listen for Hue bulbs advertising nearby.</summary>
    public static IReadOnlyList<Found> Scan(TimeSpan duration, Action<Found>? onFound = null)
    {
        var found = new Dictionary<ulong, Found>();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, e) =>
        {
            var name = e.Advertisement.LocalName;
            bool signify = e.Advertisement.ServiceUuids.Contains(HueBleProtocol.SignifyService);
            if (!HueBleProtocol.LooksLikeHue(name, signify)) return;
            lock (found)
            {
                bool isNew = !found.TryGetValue(e.BluetoothAddress, out var old);
                var entry = new Found(e.BluetoothAddress, string.IsNullOrWhiteSpace(name) ? old?.Name ?? "Hue light" : name.Trim(), e.RawSignalStrengthInDBm);
                found[e.BluetoothAddress] = entry;
                if (isNew) onFound?.Invoke(entry);
            }
        };
        watcher.Start();
        Thread.Sleep(duration);
        watcher.Stop();
        lock (found) return [.. found.Values];
    }

    /// <summary>Pair (bond) with a bulb. Returns a short status for the user.</summary>
    public static string Pair(ulong address)
    {
        using var device = Ble.Wait(BluetoothLEDevice.FromBluetoothAddressAsync(address), ConnectTimeout, "reach the bulb")
                           ?? throw new BackendException("Windows couldn't reach the bulb; move it closer and try again");
        var pairing = device.DeviceInformation.Pairing;
        if (pairing.IsPaired) return "already paired";
        if (!pairing.CanPair) return "the bulb isn't accepting pairing right now (switch it off and on, then try again within a few minutes)";
        // Hue bulbs use "just works" pairing. Accept whatever Windows asks, at the default
        // protection level; if that is refused, let Windows do its own standard pairing.
        pairing.Custom.PairingRequested += (_, e) => e.Accept();
        const DevicePairingKinds kinds = DevicePairingKinds.ConfirmOnly | DevicePairingKinds.ConfirmPinMatch | DevicePairingKinds.DisplayPin;
        var status = Ble.Wait(pairing.Custom.PairAsync(kinds, DevicePairingProtectionLevel.Default),
                              TimeSpan.FromSeconds(30), "pair with the bulb").Status;
        if (!Ok(status))
            status = Ble.Wait(pairing.PairAsync(DevicePairingProtectionLevel.Default), TimeSpan.FromSeconds(30), "pair with the bulb").Status;
        return Ok(status) ? "paired" : $"pairing failed ({status})";
    }

    private static bool Ok(DevicePairingResultStatus s) =>
        s is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired;
}

internal sealed class HueBluetoothSession(DeviceInfo info) : IDeviceSession
{
    private readonly ulong _address = (ulong)info.BackendState!;
    private BluetoothLEDevice? _device;
    private GattSession? _gatt;
    private GattDeviceService? _service;
    private GattCharacteristic? _light;
    private byte[]? _restore;
    private Rgb? _last;

    public void Connect()
    {
        _device = Ble.Wait(BluetoothLEDevice.FromBluetoothAddressAsync(_address), HueBluetoothBackend.ConnectTimeout, "reach the bulb")
                  ?? throw new BackendException("Windows can't find this bulb (is it powered and within range?)");
        if (!_device.DeviceInformation.Pairing.IsPaired)
            throw new BackendException("this bulb is no longer paired with Windows; run HueBluetooth.bat");
        _gatt = Ble.Wait(GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId), HueBluetoothBackend.ConnectTimeout, "open Bluetooth");
        if (_gatt is { CanMaintainConnection: true }) _gatt.MaintainConnection = true;
        var services = Ble.Wait(_device.GetGattServicesForUuidAsync(HueBleProtocol.LightService, BluetoothCacheMode.Uncached),
                                HueBluetoothBackend.ConnectTimeout, "connect to the bulb");
        _service = services.Status == GattCommunicationStatus.Success ? services.Services.FirstOrDefault() : null;
        if (_service is null) throw new BackendException($"the bulb didn't answer over Bluetooth ({services.Status}); is it within range?");
        var chars = Ble.Wait(_service.GetCharacteristicsForUuidAsync(HueBleProtocol.LightState, BluetoothCacheMode.Uncached),
                             HueBluetoothBackend.ConnectTimeout, "find the light control");
        _light = chars.Characteristics.FirstOrDefault() ?? throw new BackendException("this Hue model has no Bluetooth light control PitGlow knows");
    }

    public VerifyResult Verify(bool engaged) =>
        _device!.ConnectionStatus == BluetoothConnectionStatus.Connected || !engaged ? VerifyResult.Ok : VerifyResult.Moved;

    public void Acquire()
    {
        // Remember exactly how the bulb looks, to put it back afterwards.
        _restore = HueBluetoothBackend.ReadState(_service!, BluetoothCacheMode.Uncached, TimeSpan.FromSeconds(5))?.Restore;
        _last = null;
    }

    public void Write(IReadOnlyList<Rgb> frame)
    {
        var color = frame.Count > 0 ? frame[0] : Rgb.Off;
        if (_last == color) return;   // keepalive frames: Bluetooth needs no resend
        // "No flag" frames show the bulb's own state exactly, not an RGB approximation of it.
        var payload = color == info.OriginalColors[0] && _restore is { Length: > 0 } ? _restore : HueBleProtocol.Frame(color);
        Send(payload);
        _last = color;
    }

    public void Release()
    {
        if (_restore is { Length: > 0 }) Send(_restore);
        _last = null;
    }

    public void RestoreTo(string? modeName, IReadOnlyList<Rgb> colors)
    {
        if (colors.Count > 0) Send(HueBleProtocol.Frame(colors[0]));
    }

    private void Send(byte[] payload)
    {
        using var writer = new DataWriter();
        writer.WriteBytes(payload);
        var result = Ble.Wait(_light!.WriteValueWithResultAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse),
                              TimeSpan.FromSeconds(3), "send a color");
        if (result.Status != GattCommunicationStatus.Success)
            throw new BackendException(result.Status == GattCommunicationStatus.ProtocolError && result.ProtocolError == 0x05
                ? "the bulb refused the command: pair it with Windows again (HueBluetooth.bat)"
                : $"the bulb didn't take the color ({result.Status})");
    }

    public void Dispose()
    {
        _service?.Dispose();
        if (_gatt is not null) { try { _gatt.MaintainConnection = false; } catch { /* ignore */ } _gatt.Dispose(); }
        _device?.Dispose();
        _service = null;
        _gatt = null;
        _device = null;
    }
}

/// <summary>Blocking waits on Windows' async Bluetooth calls, with time limits and plain-English errors.</summary>
internal static class Ble
{
    public static T Wait<T>(IAsyncOperation<T> op, TimeSpan timeout, string what)
    {
        try
        {
            return op.AsTask().WaitAsync(timeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            try { op.Cancel(); } catch { /* ignore */ }
            throw new BackendException($"Bluetooth: timed out trying to {what}");
        }
        catch (Exception ex) when (ex is not BackendException)
        {
            throw new BackendException($"Bluetooth: couldn't {what} ({ex.Message})", ex);
        }
    }
}
