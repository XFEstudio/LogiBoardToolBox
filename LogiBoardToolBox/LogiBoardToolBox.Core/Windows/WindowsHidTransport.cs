using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using LogiBoardToolBox.Core.HidPlusPlus;
using Microsoft.Win32.SafeHandles;

namespace LogiBoardToolBox.Core.Windows;

public sealed record HidDeviceInfo(string Path, ushort ProductId, string ProductName, string SerialNumber,
    ushort UsagePage, ushort Usage, int InputReportLength, int OutputReportLength)
{
    public string DisplayName => $"{ProductName} · 046D:{ProductId:X4}";
    public string CollectionGroup => Regex.Replace(Regex.Replace(Path, "&col[0-9a-f]{2}", "", RegexOptions.IgnoreCase), "&[0-9a-f]{4}(?=#)", "", RegexOptions.IgnoreCase).ToUpperInvariant();
}

/// <summary>Uses Windows' existing HID driver; no filter driver or administrator access required.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHidTransport : IHidTransport
{
    private readonly FileStream stream;
    private readonly List<FileStream> streams = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<byte[]> reports = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    public int InputReportLength { get; }
    public int OutputReportLength { get; }
    public WindowsHidTransport(HidDeviceInfo device)
    {
        stream = OpenStream(device.Path);
        streams.Add(stream);
        InputReportLength = device.InputReportLength;
        OutputReportLength = device.OutputReportLength;
        _ = PumpAsync(stream, device.InputReportLength);
        // Windows delivers 0x10 replies through the short-report collection, even for
        // some requests sent using 0x11. Read both collections of this physical interface.
        try
        {
            foreach (var sibling in EnumerateLogitechDevices().Where(d => d.Path != device.Path && d.CollectionGroup == device.CollectionGroup && d.InputReportLength < 20))
            {
                var input = OpenStream(sibling.Path);
                streams.Add(input);
                _ = PumpAsync(input, sibling.InputReportLength);
            }
        }
        catch { Dispose(); throw; }
    }

    private static FileStream OpenStream(string path)
    {
        var handle = Native.CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "无法打开罗技 HID++ 接口。");
        }
        try { return new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true); }
        catch { handle.Dispose(); throw; }
    }

    public async Task WriteAsync(byte[] report, CancellationToken cancellationToken) =>
        await stream.WriteAsync(report, cancellationToken).ConfigureAwait(false);

    private async Task PumpAsync(FileStream input, int length)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var report = new byte[length];
                var count = await input.ReadAsync(report, lifetime.Token).ConfigureAwait(false);
                if (count == 0) throw new IOException("鼠标 HID 接口已断开。");
                await reports.Writer.WriteAsync(count == report.Length ? report : report[..count], lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
        catch (Exception ex) { reports.Writer.TryComplete(ex); }
    }
    public async Task<byte[]> ReadAsync(CancellationToken cancellationToken) => await reports.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    public void Dispose()
    {
        lifetime.Cancel();
        foreach (var input in streams) input.Dispose();
        reports.Writer.TryComplete();
    }

    public static IReadOnlyList<HidDeviceInfo> EnumerateLogitechDevices()
    {
        Native.HidD_GetHidGuid(out var hidGuid);
        var set = Native.SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var devices = new List<HidDeviceInfo>();
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new Native.DeviceInterfaceData { Size = Marshal.SizeOf<Native.DeviceInterfaceData>() };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref data))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    throw new Win32Exception(error);
                }
                Native.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var size, IntPtr.Zero);
                if (size < 8 || size > 65536) continue;
                var detail = Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(detail + 4);
                    if (path is null || !path.Contains("vid_046d", StringComparison.OrdinalIgnoreCase)) continue;
                    using var handle = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (handle.IsInvalid) continue;
                    var attributes = new Native.HidAttributes { Size = Marshal.SizeOf<Native.HidAttributes>() };
                    if (!Native.HidD_GetAttributes(handle, ref attributes) || attributes.VendorId != 0x046D) continue;
                    if (!Native.HidD_GetPreparsedData(handle, out var preparsed)) continue;
                    try
                    {
                        if (Native.HidP_GetCaps(preparsed, out var caps) < 0) continue;
                        if (caps.UsagePage < 0xFF00 || caps.InputReportByteLength < 7 || caps.OutputReportByteLength < 7) continue;
                        var name = ReadString(handle, Native.HidD_GetProductString);
                        var serial = ReadString(handle, Native.HidD_GetSerialNumberString);
                        devices.Add(new(path, attributes.ProductId, string.IsNullOrWhiteSpace(name) ? "Logitech Receiver" : name,
                            serial, caps.UsagePage, caps.Usage, caps.InputReportByteLength, caps.OutputReportByteLength));
                    }
                    finally { Native.HidD_FreePreparsedData(preparsed); }
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return devices;
    }

    private delegate bool HidStringReader(SafeFileHandle handle, StringBuilder buffer, uint length);
    private static string ReadString(SafeFileHandle handle, HidStringReader read)
    {
        var buffer = new StringBuilder(256);
        return read(handle, buffer, 512) ? buffer.ToString() : "";
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct DeviceInterfaceData { internal int Size; internal Guid ClassGuid; internal uint Flags; internal UIntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct HidAttributes { internal int Size; internal ushort VendorId; internal ushort ProductId; internal ushort Version; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct HidCaps
        {
            internal ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] internal ushort[] Reserved;
            internal ushort LinkCollectionNodes, InputButtonCaps, InputValueCaps, InputDataIndices;
            internal ushort OutputButtonCaps, OutputValueCaps, OutputDataIndices, FeatureButtonCaps, FeatureValueCaps, FeatureDataIndices;
        }
        [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HidAttributes attributes);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr data, out HidCaps caps);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetProductString(SafeFileHandle handle, StringBuilder text, uint size);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetSerialNumberString(SafeFileHandle handle, StringBuilder text, uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref DeviceInterfaceData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterfaceData data, IntPtr detail, uint size, out uint required, IntPtr device);
        [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    }
}
