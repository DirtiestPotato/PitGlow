using System.Text;
using PitGlow.Hue;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Storage.Streams;

namespace PitGlow.HueBluetooth;

/// <summary>
/// Step-by-step Bluetooth report for each paired Hue bulb: what Windows knows, whether a link
/// comes up, and what the bulb answers to a read and a test color. For working out why a bulb
/// won't take colors.
/// </summary>
public static class HueBluetoothCheck
{
    public static string Run(Action<string> say)
    {
        var r = new Reporter(say);
        var log = r.Log;
        Action<string> Say = r.Say;
        T? Try<T>(string what, Func<T> f) => r.Try(what, f);

        Say($"Hue Bluetooth check, {DateTime.Now:yyyy-MM-dd HH:mm:ss}, Windows {Environment.OSVersion.Version}");
        var adapter = Try("adapter", () => Wait(BluetoothAdapter.GetDefaultAsync()));
        if (adapter is null) { Say("No Bluetooth adapter."); return log.ToString(); }
        Say($"Adapter: LE {adapter.IsLowEnergySupported}, LE secure connections {adapter.AreLowEnergySecureConnectionsSupported}, " +
            $"central role {adapter.IsCentralRoleSupported}, address {adapter.BluetoothAddress:X12}");
        var radio = Try("radio", () => Wait(adapter.GetRadioAsync()));
        Say($"Radio: {radio?.Name} {radio?.State}");

        var selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
        IReadOnlyList<DeviceInformation> paired =
            (IReadOnlyList<DeviceInformation>?)Try("paired list", () => Wait(DeviceInformation.FindAllAsync(selector))) ?? [];
        Say($"\nPaired Bluetooth LE devices: {paired.Count}");
        foreach (var di in paired) Say($"  - {di.Name}  enabled={di.IsEnabled}  id={di.Id}");

        var bulbs = paired.Where(d => HueBleProtocol.LooksLikeHue(d.Name)).ToList();
        if (bulbs.Count == 0) Say("\nNo paired device has a Hue name.");
        foreach (var di in bulbs) CheckBulb(di, r);
        Say("\nDone.");
        return log.ToString();
    }

    private sealed class Reporter(Action<string> say)
    {
        public StringBuilder Log { get; } = new();

        public void Say(string s)
        {
            lock (Log) { say(s); Log.AppendLine(s); }
        }

        public T? Try<T>(string what, Func<T> f)
        {
            try { return f(); }
            catch (Exception ex) { Say($"    {what}: FAILED {ex.GetType().Name}: {ex.Message}"); return default; }
        }
    }

    private static void CheckBulb(DeviceInformation di, Reporter r)
    {
        Action<string> Say = r.Say;
        T? Try<T>(string what, Func<T> f) => r.Try(what, f);
        Say($"\n=== {di.Name}");
        using var device = Try("open", () => Wait(BluetoothLEDevice.FromIdAsync(di.Id)));
        if (device is null) { Say("    Windows couldn't open it."); return; }
        var p = device.DeviceInformation.Pairing;
        Say($"    address {device.BluetoothAddress:X12} ({device.BluetoothAddressType}), paired {p.IsPaired}, protection {p.ProtectionLevel}, " +
            $"can pair {p.CanPair}, connection {device.ConnectionStatus}");
        device.ConnectionStatusChanged += (d, _) => Say($"    [event] connection -> {d.ConnectionStatus}");

        var session = Try("session", () => Wait(GattSession.FromDeviceIdAsync(device.BluetoothDeviceId)));
        if (session is not null)
        {
            session.SessionStatusChanged += (_, e) => Say($"    [event] session -> {e.Status} ({e.Error})");
            Say($"    session: can maintain {session.CanMaintainConnection}, status {session.SessionStatus}, max PDU {session.MaxPduSize}");
            if (session.CanMaintainConnection) session.MaintainConnection = true;
        }

        for (int i = 0; i < 15 && device.ConnectionStatus != BluetoothConnectionStatus.Connected; i++) Thread.Sleep(1000);
        Say($"    after waiting up to 15 s: connection {device.ConnectionStatus}, session {session?.SessionStatus}");

        foreach (var mode in new[] { BluetoothCacheMode.Uncached, BluetoothCacheMode.Cached })
        {
            var all = Try($"services ({mode})", () => Wait(device.GetGattServicesAsync(mode)));
            if (all is null) continue;
            Say($"    services ({mode}): {all.Status} protocol error {all.ProtocolError}: {string.Join(", ", all.Services.Select(s => Short(s.Uuid)))}");
            foreach (var s in all.Services) s.Dispose();
        }

        var svcResult = Try("light service", () => Wait(device.GetGattServicesForUuidAsync(HueBleProtocol.LightService, BluetoothCacheMode.Cached)));
        var svc = svcResult?.Services.FirstOrDefault();
        if (svc is null) { Say($"    light service: not found ({svcResult?.Status})"); session?.Dispose(); return; }
        var access = Try("open service", () => Wait(svc.OpenAsync(GattSharingMode.SharedReadAndWrite)));
        Say($"    open light service: {access}");
        var charsResult = Try("characteristics", () => Wait(svc.GetCharacteristicsAsync(BluetoothCacheMode.Cached)));
        if (charsResult is not null)
            foreach (var c in charsResult.Characteristics)
                Say($"    char {Short(c.Uuid)}: {c.CharacteristicProperties}, protection {c.ProtectionLevel}");
        var light = charsResult?.Characteristics.FirstOrDefault(c => c.Uuid == HueBleProtocol.LightState);
        if (light is null) { Say("    light control characteristic missing"); session?.Dispose(); svc.Dispose(); return; }

        var read = Try("read", () => Wait(light.ReadValueAsync(BluetoothCacheMode.Uncached), 10));
        byte[]? before = null;
        if (read is not null)
        {
            if (read.Status == GattCommunicationStatus.Success)
            {
                before = new byte[read.Value.Length];
                using var reader = DataReader.FromBuffer(read.Value);
                reader.ReadBytes(before);
            }
            Say($"    read state: {read.Status} protocol error {read.ProtocolError} value {(before is null ? "-" : Convert.ToHexString(before))}");
        }

        foreach (var (label, option) in new[] { ("with response", GattWriteOption.WriteWithResponse), ("without response", GattWriteOption.WriteWithoutResponse) })
        {
            using var w = new DataWriter();
            w.WriteBytes(HueBleProtocol.Frame(new Rgb(0, 255, 0)));
            var result = Try($"write green {label}", () => Wait(light.WriteValueWithResultAsync(w.DetachBuffer(), option), 10));
            if (result is not null) Say($"    write green {label}: {result.Status} protocol error {result.ProtocolError}");
            Thread.Sleep(1500);
        }
        if (before is { Length: > 0 })
        {
            using var w = new DataWriter();
            w.WriteBytes(HueBleProtocol.Parse(before).Restore);
            var result = Try("restore", () => Wait(light.WriteValueWithResultAsync(w.DetachBuffer(), GattWriteOption.WriteWithResponse), 10));
            Say($"    restore: {result?.Status}");
        }
        Say($"    final: connection {device.ConnectionStatus}, session {session?.SessionStatus}");
        svc.Dispose();
        if (session is not null) { session.MaintainConnection = false; session.Dispose(); }
    }

    private static string Short(Guid g)
    {
        var s = g.ToString("D").ToUpperInvariant();
        return s.EndsWith("-0000-1000-8000-00805F9B34FB") ? s[4..8] : s;
    }

    private static T Wait<T>(Windows.Foundation.IAsyncOperation<T> op, int seconds = 15) =>
        op.AsTask().WaitAsync(TimeSpan.FromSeconds(seconds)).GetAwaiter().GetResult();
}
