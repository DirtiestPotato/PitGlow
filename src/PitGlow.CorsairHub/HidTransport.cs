using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PitGlow.Backends;

namespace PitGlow.CorsairHub;

/// <summary>Raw HID access to a Commander Core on Windows.</summary>
public sealed class HidTransport : IHubTransport
{
    /// <summary>Commander Core USB product ids this driver handles (Corsair vendor 1B1C, interface 0).</summary>
    public static readonly string[] ProductIds = ["0c32", "0c1c"];

    private readonly FileStream _stream;
    private readonly int _inLength;

    public HidTransport(string path)
    {
        var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);   // read/write, overlapped
        if (h.IsInvalid) throw new BackendException($"Corsair hub: can't open it ({new Win32Exception().Message})");
        if (!HidD_GetPreparsedData(h, out var pp)) { h.Dispose(); throw new BackendException("Corsair hub: can't read its report sizes"); }
        HidP_GetCaps(pp, out var caps);
        HidD_FreePreparsedData(pp);
        ReportLength = caps.OutputReportByteLength;
        _inLength = caps.InputReportByteLength;
        _stream = new FileStream(h, FileAccess.ReadWrite, 1, isAsync: true);
    }

    public int ReportLength { get; }

    public void Write(byte[] report) => _stream.Write(report, 0, report.Length);

    public byte[] Read(int timeoutMs)
    {
        var buf = new byte[_inLength];
        var task = _stream.ReadAsync(buf, 0, buf.Length);
        if (!task.Wait(timeoutMs)) throw new BackendException("Corsair hub: stopped answering");
        return buf;
    }

    public void Dispose() => _stream.Dispose();

    /// <summary>Device paths of every Commander Core plugged in.</summary>
    public static IReadOnlyList<string> Find()
    {
        var found = new List<string>();
        if (!OperatingSystem.IsWindows()) return found;
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);   // present, interfaces
        try
        {
            var ifd = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref ifd); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref ifd, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                var mem = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(mem, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref ifd, mem, size, out _, IntPtr.Zero)) continue;
                    var p = Marshal.PtrToStringUni(mem + 4)!;
                    if (ProductIds.Any(pid => p.Contains($"vid_1b1c&pid_{pid}&mi_00", StringComparison.OrdinalIgnoreCase))) found.Add(p);
                }
                finally { Marshal.FreeHGlobal(mem); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                      NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                      NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr w, uint f);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, uint i, ref SP_DEVICE_INTERFACE_DATA ifd);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA ifd, IntPtr detail, int size, out int required, IntPtr info);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
}
