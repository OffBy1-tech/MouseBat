using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MouseBat;

public sealed record HidInterface(string Path, ushort VendorId, ushort UsagePage, ushort Usage, int InputLength, int OutputLength);

/// <summary>Thin wrapper over the Windows SetupAPI and HID APIs.</summary>
internal static class HidNative
{
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint ShareReadWrite = 3, OpenExisting = 3, FileFlagOverlapped = 0x40000000;
    private const int DigcfPresent = 0x02, DigcfDeviceInterface = 0x10;

    public static List<HidInterface> Enumerate(ushort vendorId)
    {
        var found = new List<HidInterface>();
        HidD_GetHidGuid(out var hidGuid);

        var deviceSet = SetupDiGetClassDevsW(ref hidGuid, 0, 0, DigcfPresent | DigcfDeviceInterface);
        if (deviceSet == -1) return found;

        try
        {
            var iface = new SpDeviceInterfaceData { CbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(deviceSet, 0, ref hidGuid, i, ref iface); i++)
            {
                var path = GetInterfacePath(deviceSet, ref iface);
                if (path != null && Describe(path, vendorId) is { } info) found.Add(info);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceSet);
        }
        return found;
    }

    public static FileStream OpenAsyncStream(string path)
    {
        var handle = CreateFileW(path, GenericRead | GenericWrite, ShareReadWrite, 0, OpenExisting, FileFlagOverlapped, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"Could not open HID device (Win32 error {error}).");
        }
        return new FileStream(handle, FileAccess.ReadWrite, bufferSize: 0, isAsync: true);
    }

    private static string? GetInterfacePath(nint deviceSet, ref SpDeviceInterfaceData iface)
    {
        SetupDiGetDeviceInterfaceDetailW(deviceSet, ref iface, 0, 0, out int size, 0);
        if (size <= 0) return null;

        var detail = Marshal.AllocHGlobal(size);
        try
        {
            // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W: the DWORD plus one WCHAR, padded.
            Marshal.WriteInt32(detail, nint.Size == 8 ? 8 : 6);
            return SetupDiGetDeviceInterfaceDetailW(deviceSet, ref iface, detail, size, out _, 0)
                ? Marshal.PtrToStringUni(detail + 4)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    private static HidInterface? Describe(string path, ushort vendorId)
    {
        // Zero access rights: enough to query attributes, even for devices in use elsewhere.
        using var handle = CreateFileW(path, 0, ShareReadWrite, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid) return null;

        var attributes = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
        if (!HidD_GetAttributes(handle, ref attributes) || attributes.VendorId != vendorId) return null;
        if (!HidD_GetPreparsedData(handle, out var preparsed)) return null;

        try
        {
            if (HidP_GetCaps(preparsed, out var caps) < 0) return null;
            return new HidInterface(path, attributes.VendorId, caps.UsagePage, caps.Usage, caps.InputLength, caps.OutputLength);
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HiddAttributes
    {
        public int Size;
        public ushort VendorId, ProductId, VersionNumber;
    }

    // Only the leading fields are used; Size covers the rest of the native HIDP_CAPS.
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct HidpCaps
    {
        public ushort Usage, UsagePage, InputLength, OutputLength;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(SafeFileHandle device, ref HiddAttributes attributes);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out nint preparsed);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(nint preparsed);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(nint preparsed, out HidpCaps caps);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern nint SetupDiGetClassDevsW(ref Guid classGuid, nint enumerator, nint parent, int flags);
    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(nint deviceSet);
    [DllImport("setupapi.dll")]
    private static extern bool SetupDiEnumDeviceInterfaces(nint deviceSet, nint deviceInfo, ref Guid classGuid, uint index, ref SpDeviceInterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(nint deviceSet, ref SpDeviceInterfaceData data, nint detail, int detailSize, out int requiredSize, nint deviceInfo);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
}
