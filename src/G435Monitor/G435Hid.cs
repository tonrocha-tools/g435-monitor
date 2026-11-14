using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace G435Monitor;

/// <summary>
/// Acesso HID somente à interface de fornecedor do G435 (046D:0ADF, MI_00,
/// usage page 0xFF00, in/out 65). Nenhum outro dispositivo é aberto.
/// </summary>
internal static class G435Hid
{
    public const ushort TargetVid = 0x046D;
    public const ushort TargetPid = 0x0ADF;
    public const ushort TargetUsagePage = 0xFF00;
    public const ushort TargetReportLength = 65;

    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;
    private const uint FILE_SHARE_READ = 0x01;
    private const uint FILE_SHARE_WRITE = 0x02;
    private const uint OPEN_EXISTING = 3;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const int ERROR_NO_MORE_ITEMS = 259;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    public static string? FindDevicePath(out string? error)
    {
        error = null;

        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr set = SetupDiGetClassDevs(
            ref hidGuid, IntPtr.Zero, IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

        if (set == new IntPtr(-1))
        {
            error = $"SetupDiGetClassDevs falhou ({Marshal.GetLastWin32Error()}).";
            return null;
        }

        try
        {
            uint index = 0;
            while (true)
            {
                var data = new SP_DEVICE_INTERFACE_DATA
                {
                    cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>()
                };

                if (!SetupDiEnumDeviceInterfaces(
                        set, IntPtr.Zero, ref hidGuid, index, ref data))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != ERROR_NO_MORE_ITEMS)
                        error = $"SetupDiEnumDeviceInterfaces falhou ({err}).";
                    break;
                }

                index++;
                string? path = GetDevicePath(set, ref data);
                if (path is null)
                    continue;

                // Só o dongle do G435, e só a coleção de fornecedor.
                if (!path.Contains("vid_046d", StringComparison.OrdinalIgnoreCase) ||
                    !path.Contains("pid_0adf", StringComparison.OrdinalIgnoreCase))
                    continue;

                Caps? caps = QueryCaps(path);
                if (caps is not null
                    && caps.Value.UsagePage == TargetUsagePage
                    && caps.Value.InputLength == TargetReportLength
                    && caps.Value.OutputLength == TargetReportLength)
                {
                    return path;
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        error = "Dongle 046D:0ADF com interface 0xFF00 (65/65) não encontrado. " +
                "Verifique se o receptor LIGHTSPEED está plugado.";
        return null;
    }

    public static SafeFileHandle OpenForReadWrite(string path) =>
        CreateFileW(
            path,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);

    public static bool WriteReport(SafeFileHandle handle, byte[] frame) =>
        WriteFile(handle, frame, (uint)frame.Length, out _, IntPtr.Zero);

    public static bool GetInputReport(SafeFileHandle handle, byte[] buffer) =>
        HidD_GetInputReport(handle, buffer, (uint)buffer.Length);

    private static string? GetDevicePath(
        IntPtr set, ref SP_DEVICE_INTERFACE_DATA data)
    {
        if (!SetupDiGetDeviceInterfaceDetail(
                set, ref data, IntPtr.Zero, 0, out uint required, IntPtr.Zero))
        {
            if (Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER)
                return null;
        }

        if (required == 0)
            return null;

        IntPtr detail = Marshal.AllocHGlobal((int)required);
        try
        {
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(
                    set, ref data, detail, required, out _, IntPtr.Zero))
            {
                return null;
            }

            return Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    private readonly record struct Caps(
        ushort UsagePage, ushort InputLength, ushort OutputLength);

    private static Caps? QueryCaps(string path)
    {
        SafeFileHandle handle = CreateFileW(
            path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
            OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            handle = CreateFileW(
                path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
                OPEN_EXISTING, 0, IntPtr.Zero);
        }

        if (handle.IsInvalid)
            return null;

        try
        {
            if (!HidD_GetPreparsedData(handle, out IntPtr preparsed))
                return null;

            try
            {
                IntPtr caps = Marshal.AllocHGlobal(256);
                try
                {
                    for (int i = 0; i < 256; i++)
                        Marshal.WriteByte(caps, i, 0);

                    if (HidP_GetCaps(preparsed, caps) != HIDP_STATUS_SUCCESS)
                        return null;

                    ushort usagePage = (ushort)Marshal.ReadInt16(caps, 2);
                    ushort input = (ushort)Marshal.ReadInt16(caps, 4);
                    ushort output = (ushort)Marshal.ReadInt16(caps, 6);
                    return new Caps(usagePage, input, output);
                }
                finally
                {
                    Marshal.FreeHGlobal(caps);
                }
            }
            finally
            {
                HidD_FreePreparsedData(preparsed);
            }
        }
        finally
        {
            handle.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [DllImport("hid.dll", SetLastError = true)]
    private static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(
        SafeFileHandle handle, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern int HidP_GetCaps(IntPtr preparsedData, IntPtr capabilities);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetInputReport(
        SafeFileHandle handle, [Out] byte[] buffer, uint bufferLength);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize,
        out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        SafeFileHandle handle, byte[] buffer, uint numberOfBytesToWrite,
        out uint numberOfBytesWritten, IntPtr overlapped);
}
