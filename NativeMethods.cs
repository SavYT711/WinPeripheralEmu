using System.Runtime.InteropServices;

namespace BlePeripheralEmu;

[StructLayout(LayoutKind.Sequential)]
struct POINT
{
    public int x;
    public int y;
}

/// <summary>
/// Win32 / HID P/Invoke surface used by <see cref="InputBridgeForm"/>.
/// Everything here is a straight mapping of the platform headers - no
/// behaviour lives in this file.
/// </summary>
static class NativeMethods
{
    // --- Windows hooks ---
    public const int WH_MOUSE_LL = 14;
    public const int WH_KEYBOARD_LL = 13;

    // --- Window messages ---
    public const int WM_INPUT = 0x00FF;
    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_MBUTTONDOWN = 0x0207;
    public const int WM_MBUTTONUP = 0x0208;
    public const int WM_MOUSEWHEEL = 0x020A;
    public const int WM_MOUSEHWHEEL = 0x020E;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;

    /// <summary>One wheel notch, as reported in the high word of WM_MOUSEWHEEL's mouseData.</summary>
    public const int WHEEL_DELTA = 120;

    // --- Raw Input ---
    public const uint RIDEV_INPUTSINK = 0x00000100;
    public const uint RIDEV_REMOVE = 0x00000001;
    public const uint RID_INPUT = 0x10000003;
    public const int RIM_TYPEMOUSE = 0;
    public const int RIM_TYPEHID = 2;
    public const uint RIDI_PREPARSEDDATA = 0x20000005;

    public const ushort RI_MOUSE_LEFT_BUTTON_DOWN = 0x0001;
    public const ushort RI_MOUSE_LEFT_BUTTON_UP = 0x0002;
    public const ushort RI_MOUSE_RIGHT_BUTTON_DOWN = 0x0004;
    public const ushort RI_MOUSE_RIGHT_BUTTON_UP = 0x0008;
    public const ushort RI_MOUSE_MIDDLE_BUTTON_DOWN = 0x0010;
    public const ushort RI_MOUSE_MIDDLE_BUTTON_UP = 0x0020;
    public const ushort RI_MOUSE_WHEEL = 0x0400;
    public const ushort RI_MOUSE_HWHEEL = 0x0800;

    /// <summary>Set when a mouse reports absolute coordinates (tablets, some VMs, RDP).</summary>
    public const ushort MOUSE_MOVE_ABSOLUTE = 0x0001;

    // --- HID parsing ---
    public const uint HIDP_STATUS_SUCCESS = 0x00110000;

    // HID usage pages/usages we care about.
    public const ushort UsagePageGenericDesktop = 0x01;
    public const ushort UsagePageDigitizer = 0x0D;
    public const ushort UsageMouse = 0x02;
    public const ushort UsageTouchPad = 0x05;
    public const ushort UsageX = 0x30;
    public const ushort UsageY = 0x31;
    public const ushort UsageContactId = 0x51;
    public const ushort UsageContactCount = 0x54;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct RAWMOUSE
    {
        [FieldOffset(0)] public ushort usFlags;
        [FieldOffset(4)] public ushort usButtonFlags;
        [FieldOffset(6)] public ushort usButtonData;
        [FieldOffset(8)] public uint ulRawButtons;
        [FieldOffset(12)] public int lLastX;
        [FieldOffset(16)] public int lLastY;
        [FieldOffset(20)] public uint ulExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUT
    {
        public RAWINPUTHEADER header;
        public RAWMOUSE mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWHID
    {
        public uint dwSizeHid;
        public uint dwCount;
        // dwCount reports of dwSizeHid bytes each follow immediately in
        // memory - not represented here; extracted separately by offset.
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUT_HID
    {
        public RAWINPUTHEADER header;
        public RAWHID hid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    public enum HIDP_REPORT_TYPE
    {
        HidP_Input,
        HidP_Output,
        HidP_Feature
    }

    // The native struct has a union (Range / NotRange) occupying the same
    // bytes; only the Range-style fields are declared. UsageMin and the
    // NotRange "Usage" field share an offset, so the alias below is free.
    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_VALUE_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        [MarshalAs(UnmanagedType.U1)] public bool IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        [MarshalAs(UnmanagedType.U1)] public bool IsRange;
        [MarshalAs(UnmanagedType.U1)] public bool IsStringRange;
        [MarshalAs(UnmanagedType.U1)] public bool IsDesignatorRange;
        [MarshalAs(UnmanagedType.U1)] public bool IsAbsolute;
        [MarshalAs(UnmanagedType.U1)] public bool HasNull;
        public byte Reserved;
        public ushort BitSize;
        public ushort ReportCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
        public ushort[] Reserved2;
        public uint UnitsExp;
        public uint Units;
        public int LogicalMin;
        public int LogicalMax;
        public int PhysicalMin;
        public int PhysicalMax;
        public ushort UsageMin;
        public ushort UsageMax;
        public ushort StringMin;
        public ushort StringMax;
        public ushort DesignatorMin;
        public ushort DesignatorMax;
        public ushort DataIndexMin;
        public ushort DataIndexMax;

        public readonly ushort Usage => UsageMin;
    }

    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll")]
    public static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    [DllImport("hid.dll")]
    public static extern uint HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

    [DllImport("hid.dll")]
    public static extern uint HidP_GetValueCaps(HIDP_REPORT_TYPE reportType, [Out] HIDP_VALUE_CAPS[] valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);

    [DllImport("hid.dll")]
    public static extern uint HidP_GetUsageValue(HIDP_REPORT_TYPE reportType, ushort usagePage, ushort linkCollection, ushort usage, out uint usageValue, IntPtr preparsedData, IntPtr report, uint reportLength);
}
