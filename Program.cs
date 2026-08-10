using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

// ---------------------------------------------------------------------
// BLE HID-over-GATT (HOGP) peripheral - combo keyboard + mouse - plus a
// real trackpad/keyboard passthrough bridge with edge-of-screen handoff.
//
// A settings window (SettingsForm) lets you pick which screen edge
// triggers the handoff, capture any key as the return-to-Windows
// hotkey, and toggle auto-return. See InputBridgeForm for how the
// handoff/suppression/Raw-Input/auto-return plumbing works.
//
// This build is a real Windows executable (no console) - status lives in
// a system tray icon instead, and all diagnostic output goes to
// debug.log next to the .exe (there's no console to print to anymore).
//
// Known simplification: writes to Protocol Mode aren't persisted back
// (it's created as a static Report-Protocol value). Harmless for this
// test - only matters if a host explicitly switches to Boot Protocol.
// ---------------------------------------------------------------------

// Registered first, before anything below that could throw, so we have
// the best chance of catching and logging whatever goes wrong.
AppDomain.CurrentDomain.UnhandledException += (_, args) =>
{
    Logger.Log($"[FATAL] Unhandled exception: {args.ExceptionObject}");
};
Application.ThreadException += (_, args) =>
{
    Logger.Log($"[FATAL] WinForms thread exception: {args.Exception}");
    Environment.Exit(1);
};

// Standard WinForms init - MUST happen before any window (IWin32Window)
// gets created anywhere in the process, including indirectly. This bit
// us once already: WindowsFormsSynchronizationContext's constructor
// creates a hidden message-only window internally, and creating that
// before SetCompatibleTextRenderingDefault() threw
// "SetCompatibleTextRenderingDefault must be called before the first
// IWin32Window object is created" - a real crash, not a WinForms
// integration problem. Order here matters: these two calls first, then
// the sync context.
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

// (No WindowsFormsSynchronizationContext here on purpose - it was tried
// and reverted. Its own constructor creates a hidden window and relies on
// something pumping messages for it, but nothing does until much later
// (SettingsForm.ShowDialog() / Application.Run()), so any await before
// that point would queue a continuation that never gets serviced - a
// real deadlock, not a fix. SettingsForm.ShowDialog() and
// Application.Run(bridgeForm) below have no await between them, so
// they're guaranteed to land on the same thread as each other regardless
// of which thread that ends up being - that's the actual requirement,
// and it doesn't need a synchronization context to hold.)

var hidServiceUuid       = BluetoothUuidHelper.FromShortId(0x1812);
var hidInfoUuid          = BluetoothUuidHelper.FromShortId(0x2A4A);
var reportMapUuid        = BluetoothUuidHelper.FromShortId(0x2A4B);
var hidControlPointUuid  = BluetoothUuidHelper.FromShortId(0x2A4C);
var reportUuid           = BluetoothUuidHelper.FromShortId(0x2A4D);
var protocolModeUuid     = BluetoothUuidHelper.FromShortId(0x2A4E);
var reportReferenceUuid  = BluetoothUuidHelper.FromShortId(0x2908);

var deviceInfoServiceUuid = BluetoothUuidHelper.FromShortId(0x180A);
var manufacturerNameUuid  = BluetoothUuidHelper.FromShortId(0x2A29);
var pnpIdUuid              = BluetoothUuidHelper.FromShortId(0x2A50);

var batteryServiceUuid = BluetoothUuidHelper.FromShortId(0x180F);
var batteryLevelUuid   = BluetoothUuidHelper.FromShortId(0x2A19);

// Combo HID report descriptor: keyboard (Report ID 1) + mouse (Report ID 2).
// This is standard USB-HID/Bluetooth-SIG descriptor structure, the same
// shape used by essentially every DIY BLE keyboard/mouse project.
byte[] reportMap =
{
    // --- Keyboard (Report ID 1) ---
    0x05, 0x01,       // Usage Page (Generic Desktop)
    0x09, 0x06,       // Usage (Keyboard)
    0xA1, 0x01,       // Collection (Application)
    0x85, 0x01,       //   Report Id (1)
    0x05, 0x07,       //   Usage Page (Key Codes)
    0x19, 0xE0,       //   Usage Minimum (224)
    0x29, 0xE7,       //   Usage Maximum (231)
    0x15, 0x00,       //   Logical Minimum (0)
    0x25, 0x01,       //   Logical Maximum (1)
    0x75, 0x01,       //   Report Size (1)
    0x95, 0x08,       //   Report Count (8)
    0x81, 0x02,       //   Input (modifier byte)
    0x95, 0x01,       //   Report Count (1)
    0x75, 0x08,       //   Report Size (8)
    0x81, 0x01,       //   Input (reserved byte)
    0x95, 0x06,       //   Report Count (6)
    0x75, 0x08,       //   Report Size (8)
    0x15, 0x00,       //   Logical Minimum (0)
    0x25, 0x65,       //   Logical Maximum (101)
    0x05, 0x07,       //   Usage Page (Key Codes)
    0x19, 0x00,       //   Usage Minimum (0)
    0x29, 0x65,       //   Usage Maximum (101)
    0x81, 0x00,       //   Input (key array, up to 6 simultaneous keys)
    0xC0,             // End Collection

    // --- Mouse (Report ID 2) ---
    0x05, 0x01,       // Usage Page (Generic Desktop)
    0x09, 0x02,       // Usage (Mouse)
    0xA1, 0x01,       // Collection (Application)
    0x85, 0x02,       //   Report Id (2)
    0x09, 0x01,       //   Usage (Pointer)
    0xA1, 0x00,       //   Collection (Physical)
    0x05, 0x09,       //     Usage Page (Buttons)
    0x19, 0x01,       //     Usage Minimum (Button 1)
    0x29, 0x03,       //     Usage Maximum (Button 3)
    0x15, 0x00,       //     Logical Minimum (0)
    0x25, 0x01,       //     Logical Maximum (1)
    0x95, 0x03,       //     Report Count (3)
    0x75, 0x01,       //     Report Size (1)
    0x81, 0x02,       //     Input (3 button bits)
    0x95, 0x01,       //     Report Count (1)
    0x75, 0x05,       //     Report Size (5)
    0x81, 0x01,       //     Input (padding)
    0x05, 0x01,       //     Usage Page (Generic Desktop)
    0x09, 0x30,       //     Usage (X)
    0x09, 0x31,       //     Usage (Y)
    0x09, 0x38,       //     Usage (Wheel)
    0x15, 0x81,       //     Logical Minimum (-127)
    0x25, 0x7F,       //     Logical Maximum (127)
    0x75, 0x08,       //     Report Size (8)
    0x95, 0x03,       //     Report Count (3)
    0x81, 0x06,       //     Input (X, Y, Wheel - relative)
    0xC0,             //   End Collection
    0xC0              // End Collection
};

Logger.Log("Checking Bluetooth adapter...");
var adapter = await BluetoothAdapter.GetDefaultAsync();
if (adapter is null || !adapter.IsPeripheralRoleSupported)
{
    Logger.Log("Peripheral role not available on this adapter. Stopping.");
    MessageBox.Show(
        "This PC's Bluetooth adapter doesn't support peripheral mode, or no adapter was found.\n\n" +
        "Check debug.log for details.",
        "iPad Bridge - Can't start", MessageBoxButtons.OK, MessageBoxIcon.Error);
    return;
}

Logger.Log("Creating HID service...");
var hidResult = await GattServiceProvider.CreateAsync(hidServiceUuid);
if (hidResult.Error != BluetoothError.Success)
{
    Logger.Log($"Failed to create HID service: {hidResult.Error}");
    MessageBox.Show(
        $"Couldn't start the Bluetooth HID service: {hidResult.Error}\n\n" +
        (hidResult.Error == BluetoothError.RadioNotAvailable
            ? "This usually means Bluetooth is turned off. Turn it on and try again."
            : "Check debug.log for details."),
        "iPad Bridge - Can't start", MessageBoxButtons.OK, MessageBoxIcon.Error);
    return;
}
var hidProvider = hidResult.ServiceProvider;
var hidService = hidProvider.Service;

// HID Information: bcdHID=1.11, country=0, flags=NormallyConnectable
var hidInfoWriter = new DataWriter();
hidInfoWriter.WriteBytes(new byte[] { 0x11, 0x01, 0x00, 0x02 });
await HidHelpers.CreateReadCharacteristic(hidService, hidInfoUuid, hidInfoWriter.DetachBuffer(), GattProtectionLevel.EncryptionRequired);

// Report Map
var reportMapWriter = new DataWriter();
reportMapWriter.WriteBytes(reportMap);
await HidHelpers.CreateReadCharacteristic(hidService, reportMapUuid, reportMapWriter.DetachBuffer(), GattProtectionLevel.EncryptionRequired);

// Protocol Mode: 1 = Report Protocol
var protocolWriter = new DataWriter();
protocolWriter.WriteByte(1);
var protocolParams = new GattLocalCharacteristicParameters
{
    CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.WriteWithoutResponse,
    ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
    WriteProtectionLevel = GattProtectionLevel.EncryptionRequired,
    StaticValue = protocolWriter.DetachBuffer()
};
await hidService.CreateCharacteristicAsync(protocolModeUuid, protocolParams);

// HID Control Point: write-only, ignored
var controlPointParams = new GattLocalCharacteristicParameters
{
    CharacteristicProperties = GattCharacteristicProperties.WriteWithoutResponse,
    WriteProtectionLevel = GattProtectionLevel.EncryptionRequired
};
await hidService.CreateCharacteristicAsync(hidControlPointUuid, controlPointParams);

// Keyboard input report (Report ID 1) and mouse input report (Report ID 2)
var keyboardReportChar = await HidHelpers.CreateInputReportCharacteristic(hidService, reportUuid, reportReferenceUuid, reportId: 1);
var mouseReportChar    = await HidHelpers.CreateInputReportCharacteristic(hidService, reportUuid, reportReferenceUuid, reportId: 2);

Logger.Log("Creating Device Information service...");
var devInfoResult = await GattServiceProvider.CreateAsync(deviceInfoServiceUuid);
if (devInfoResult.Error == BluetoothError.Success)
{
    var devInfoService = devInfoResult.ServiceProvider.Service;

    var mfgWriter = new DataWriter();
    mfgWriter.WriteString("DIY");
    await HidHelpers.CreateReadCharacteristic(devInfoService, manufacturerNameUuid, mfgWriter.DetachBuffer());

    // PnP ID: vendor id source(2=USB-IF), vendor id, product id, product version.
    // 0xFFFF is the USB-IF reserved placeholder for non-commercial/prototype use.
    var pnpWriter = new DataWriter();
    pnpWriter.WriteByte(0x02);
    pnpWriter.WriteUInt16(0xFFFF);
    pnpWriter.WriteUInt16(0x0001);
    pnpWriter.WriteUInt16(0x0001);
    await HidHelpers.CreateReadCharacteristic(devInfoService, pnpIdUuid, pnpWriter.DetachBuffer());
}

Logger.Log("Creating Battery service...");
var batteryResult = await GattServiceProvider.CreateAsync(batteryServiceUuid);
if (batteryResult.Error == BluetoothError.Success)
{
    var batteryWriter = new DataWriter();
    batteryWriter.WriteByte(100);
    await HidHelpers.CreateReadCharacteristic(batteryResult.ServiceProvider.Service, batteryLevelUuid, batteryWriter.DetachBuffer());
}

hidProvider.AdvertisementStatusChanged += (_, args) =>
    Logger.Log($"Advertisement status: {args.Status}");

hidProvider.StartAdvertising(new GattServiceProviderAdvertisingParameters
{
    IsDiscoverable = true,
    IsConnectable = true
});

Logger.Log("");
Logger.Log("Opening settings window...");

var settings = new SettingsForm();
if (settings.ShowDialog() != DialogResult.OK)
{
    Logger.Log("Cancelled.");
    hidProvider.StopAdvertising();
    return;
}

Logger.Log($"Edge: {settings.SelectedEdge}, return key: {settings.ReturnHotkey}, auto-return: {settings.AutoReturnEnabled}");
Logger.Log("Right-click the tray icon and choose Exit to quit.");

Application.Run(new InputBridgeForm(keyboardReportChar, mouseReportChar, settings.SelectedEdge, (int)settings.ReturnHotkey, settings.AutoReturnEnabled));

hidProvider.StopAdvertising();
Logger.Log("Stopped.");

// --- helpers ---

static class Logger
{
    static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "debug.log");
    static readonly object Lock = new();

    public static void Log(string message)
    {
        lock (Lock)
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
    }
}

static class HidHelpers
{
    public static async Task<GattLocalCharacteristic> CreateReadCharacteristic(GattLocalService service, Guid uuid, IBuffer value, GattProtectionLevel protectionLevel = GattProtectionLevel.Plain)
    {
        var parameters = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read,
            ReadProtectionLevel = protectionLevel,
            StaticValue = value
        };
        var result = await service.CreateCharacteristicAsync(uuid, parameters);
        if (result.Error != BluetoothError.Success)
            throw new InvalidOperationException($"Failed to create characteristic {uuid}: {result.Error}");
        return result.Characteristic;
    }

    public static async Task<GattLocalCharacteristic> CreateInputReportCharacteristic(GattLocalService service, Guid reportUuid, Guid reportReferenceUuid, byte reportId)
    {
        var parameters = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
            ReadProtectionLevel = GattProtectionLevel.EncryptionRequired
        };
        var result = await service.CreateCharacteristicAsync(reportUuid, parameters);
        if (result.Error != BluetoothError.Success)
            throw new InvalidOperationException($"Failed to create report characteristic: {result.Error}");

        var characteristic = result.Characteristic;

        // Report Reference descriptor: [Report ID, Report Type = 1 (Input)]
        // Also encrypted - required for iOS to treat this as a legitimate HID report.
        var refWriter = new DataWriter();
        refWriter.WriteByte(reportId);
        refWriter.WriteByte(0x01);
        var descriptorParams = new GattLocalDescriptorParameters
        {
            ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
            StaticValue = refWriter.DetachBuffer()
        };
        await characteristic.CreateDescriptorAsync(reportReferenceUuid, descriptorParams);

        return characteristic;
    }

    public static async Task SendMouseReport(GattLocalCharacteristic characteristic, byte buttons, sbyte dx, sbyte dy, sbyte wheel = 0)
    {
        var writer = new DataWriter();
        writer.WriteByte(buttons);
        writer.WriteByte((byte)dx);
        writer.WriteByte((byte)dy);
        writer.WriteByte((byte)wheel);
        await characteristic.NotifyValueAsync(writer.DetachBuffer());
    }

    public static async Task SendKeyboardReport(GattLocalCharacteristic characteristic, byte modifiers, byte[] usages)
    {
        var writer = new DataWriter();
        writer.WriteByte(modifiers);
        writer.WriteByte(0); // reserved
        for (int i = 0; i < 6; i++)
            writer.WriteByte(i < usages.Length ? usages[i] : (byte)0);
        await characteristic.NotifyValueAsync(writer.DetachBuffer());
    }
}

enum ScreenEdge { Left, Right, Top, Bottom }

[StructLayout(LayoutKind.Sequential)]
struct POINT { public int x; public int y; }

// ---------------------------------------------------------------------
// Small startup dialog: pick which edge triggers the handoff, capture
// any key as the return-to-Windows hotkey, and toggle auto-return.
// ---------------------------------------------------------------------
class SettingsForm : Form
{
    public ScreenEdge SelectedEdge { get; private set; } = ScreenEdge.Right;
    public Keys ReturnHotkey { get; private set; } = Keys.F9;
    public bool AutoReturnEnabled { get; private set; } = true;

    readonly RadioButton _rbLeft, _rbRight, _rbTop, _rbBottom;
    readonly Label _hotkeyLabel;
    readonly CheckBox _autoReturnCheck;
    bool _capturingHotkey = false;

    public SettingsForm()
    {
        Text = "iPad Bridge Setup";
        Width = 380;
        Height = 340;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        var edgeGroup = new GroupBox { Text = "Which edge hands off to the iPad?", Left = 15, Top = 15, Width = 335, Height = 90 };
        _rbLeft = new RadioButton { Text = "Left", Left = 15, Top = 25, Width = 140 };
        _rbRight = new RadioButton { Text = "Right", Left = 15, Top = 55, Width = 140, Checked = true };
        _rbTop = new RadioButton { Text = "Top", Left = 175, Top = 25, Width = 140 };
        _rbBottom = new RadioButton { Text = "Bottom", Left = 175, Top = 55, Width = 140 };
        edgeGroup.Controls.Add(_rbLeft);
        edgeGroup.Controls.Add(_rbRight);
        edgeGroup.Controls.Add(_rbTop);
        edgeGroup.Controls.Add(_rbBottom);

        var hotkeyGroup = new GroupBox { Text = "Return-to-Windows key", Left = 15, Top = 115, Width = 335, Height = 85 };
        _hotkeyLabel = new Label { Text = $"Current: {ReturnHotkey}", Left = 15, Top = 25, Width = 300 };
        var setHotkeyButton = new Button { Text = "Click, then press a key...", Left = 15, Top = 48, Width = 180 };
        setHotkeyButton.Click += (_, _) =>
        {
            _capturingHotkey = true;
            _hotkeyLabel.Text = "Press any key now...";
            // Disabled while capturing so the button itself can't intercept
            // Enter/Space as a click before our KeyDown handler sees it.
            setHotkeyButton.Enabled = false;
        };
        hotkeyGroup.Controls.Add(_hotkeyLabel);
        hotkeyGroup.Controls.Add(setHotkeyButton);

        _autoReturnCheck = new CheckBox
        {
            Text = "Also auto-return when I move back toward\nthe edge (no need to hit the hotkey)",
            Left = 15,
            Top = 210,
            Width = 335,
            Height = 40,
            Checked = true
        };

        var startButton = new Button { Text = "Start", Left = 255, Top = 260, Width = 95 };
        startButton.Click += (_, _) =>
        {
            SelectedEdge = _rbLeft.Checked ? ScreenEdge.Left
                         : _rbTop.Checked ? ScreenEdge.Top
                         : _rbBottom.Checked ? ScreenEdge.Bottom
                         : ScreenEdge.Right;
            AutoReturnEnabled = _autoReturnCheck.Checked;
            DialogResult = DialogResult.OK;
            Close();
        };

        KeyDown += (_, e) =>
        {
            if (_capturingHotkey)
            {
                ReturnHotkey = e.KeyCode;
                Logger.Log($"[captured hotkey: {ReturnHotkey} = 0x{(int)ReturnHotkey:X2}]");
                _hotkeyLabel.Text = $"Current: {ReturnHotkey}";
                _capturingHotkey = false;
                setHotkeyButton.Enabled = true;
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };

        Controls.Add(edgeGroup);
        Controls.Add(hotkeyGroup);
        Controls.Add(_autoReturnCheck);
        Controls.Add(startButton);
    }
}

// ---------------------------------------------------------------------
// Full-screen semi-transparent overlay shown during corner calibration.
// Purely visual - the actual corner clicks are still captured by the
// global low-level mouse hook in InputBridgeForm (which suppresses them
// system-wide during calibration), so this form never needs to handle
// clicks itself. It just reflects calibration state as it changes.
// ---------------------------------------------------------------------
class CalibrationOverlayForm : Form
{
    readonly Label _instructionLabel;
    readonly Label _progressLabel;
    readonly Label _hintLabel;

    public int MarkedCount = 0;
    public POINT[]? Corners;

    public CalibrationOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen!.Bounds;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        Opacity = 0.55;

        _progressLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 14, FontStyle.Regular),
            ForeColor = Color.Gainsboro,
            BackColor = Color.Transparent,
            Location = new Point(40, 40)
        };
        Controls.Add(_progressLabel);

        _instructionLabel = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 30, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Dock = DockStyle.Fill
        };
        Controls.Add(_instructionLabel);

        _hintLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 11, FontStyle.Italic),
            ForeColor = Color.LightGray,
            BackColor = Color.Transparent,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left
        };
        Controls.Add(_hintLabel);
        Resize += (_, _) => _hintLabel.Location = new Point(40, ClientSize.Height - 60);
        _hintLabel.Location = new Point(40, ClientSize.Height - 60);
        _hintLabel.Text = "All mouse and keyboard input is captured until every corner is marked.";
    }

    public void UpdateStep(int step, string cornerName)
    {
        _progressLabel.Text = $"Corner {step + 1} of 4";
        _instructionLabel.Text = $"Move to the {cornerName} corner\nand click";
        Invalidate();
    }

    public void ShowComplete()
    {
        _progressLabel.Text = "Done";
        _instructionLabel.Text = "All set!";
        _hintLabel.Text = "Closing in a moment...";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Corners is null) return;
        using var brush = new SolidBrush(Color.LimeGreen);
        using var pen = new Pen(Color.White, 2);
        for (int i = 0; i < MarkedCount && i < Corners.Length; i++)
        {
            var c = Corners[i];
            e.Graphics.FillEllipse(brush, c.x - 8, c.y - 8, 16, 16);
            e.Graphics.DrawEllipse(pen, c.x - 8, c.y - 8, 16, 16);
        }
    }
}

// ---------------------------------------------------------------------
// Hidden window hosting the input bridge: global mouse/keyboard hooks
// for edge detection + suppression, Raw Input for unclamped mouse
// deltas once redirected, a configurable return-to-Windows hotkey, and
// virtual-position-based auto-return.
// ---------------------------------------------------------------------
class InputBridgeForm : Form
{
    const int WH_MOUSE_LL = 14;
    const int WH_KEYBOARD_LL = 13;
    const int WM_INPUT = 0x00FF;
    const int WM_MOUSEMOVE = 0x0200;
    const int WM_LBUTTONDOWN = 0x0201;
    const int WM_MOUSEWHEEL = 0x020A;
    const int WM_KEYDOWN = 0x0100;
    const int WM_KEYUP = 0x0101;
    const int WM_SYSKEYDOWN = 0x0104;
    const int WM_SYSKEYUP = 0x0105;

    // Divides raw per-frame touchpad contact movement (raw HID logical
    // units, NOT pixels - these can be a much finer scale than screen
    // pixels) down to a small wheel step. Untested starting guess - watch
    // the [2-finger scroll: deltaY=...] console output and adjust.
    const int ScrollScaleDivisor = 20;

    // Net "outward" delta (raw HID units) needed before auto-return
    // triggers, if enabled. Tune to taste - too low and it'll trigger on
    // small correction movements, too high and it won't feel responsive.
    const int AutoReturnThreshold = 500;

    const uint RIDEV_INPUTSINK = 0x00000100;
    const uint RID_INPUT = 0x10000003;
    const int RIM_TYPEMOUSE = 0;
    const int RIM_TYPEHID = 2;
    const uint HIDP_STATUS_SUCCESS = 0x00110000;
    const uint RIDI_PREPARSEDDATA = 0x20000005;
    const uint RIDI_DEVICEINFO = 0x2000000b;

    const ushort RI_MOUSE_LEFT_BUTTON_DOWN = 0x0001;
    const ushort RI_MOUSE_LEFT_BUTTON_UP = 0x0002;
    const ushort RI_MOUSE_RIGHT_BUTTON_DOWN = 0x0004;
    const ushort RI_MOUSE_RIGHT_BUTTON_UP = 0x0008;
    const ushort RI_MOUSE_MIDDLE_BUTTON_DOWN = 0x0010;
    const ushort RI_MOUSE_MIDDLE_BUTTON_UP = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct RAWMOUSE
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
    struct RAWINPUT
    {
        public RAWINPUTHEADER header;
        public RAWMOUSE mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWHID
    {
        public uint dwSizeHid;
        public uint dwCount;
        // Variable-length raw report bytes follow immediately in memory -
        // not represented here; extracted separately by offset.
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUT_HID
    {
        public RAWINPUTHEADER header;
        public RAWHID hid;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICELIST
    {
        public IntPtr hDevice;
        public uint dwType;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RID_DEVICE_INFO_HID
    {
        public uint dwVendorId;
        public uint dwProductId;
        public uint dwVersionNumber;
        public ushort usUsagePage;
        public ushort usUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RID_DEVICE_INFO
    {
        public uint cbSize;
        public uint dwType;
        public RID_DEVICE_INFO_HID hid;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
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

    enum HIDP_REPORT_TYPE { HidP_Input, HidP_Output, HidP_Feature }

    // The native struct has a union (Range / NotRange) occupying the same
    // bytes; only the Range-style fields are declared, with zero-cost
    // aliases below for the NotRange interpretation (same memory).
    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_VALUE_CAPS
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

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")]
    static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);
    [DllImport("user32.dll")]
    static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, ref RID_DEVICE_INFO pData, ref uint pcbSize);
    [DllImport("hid.dll")]
    static extern uint HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);
    [DllImport("hid.dll")]
    static extern uint HidP_GetValueCaps(HIDP_REPORT_TYPE reportType, [Out] HIDP_VALUE_CAPS[] valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);
    [DllImport("hid.dll")]
    static extern uint HidP_GetUsageValue(HIDP_REPORT_TYPE reportType, ushort usagePage, ushort linkCollection, ushort usage, out uint usageValue, IntPtr preparsedData, IntPtr report, uint reportLength);

    readonly GattLocalCharacteristic _keyboardReport;
    readonly GattLocalCharacteristic _mouseReport;

    IntPtr _mouseHookId = IntPtr.Zero;
    IntPtr _keyboardHookId = IntPtr.Zero;
    readonly HookProc _mouseProc;
    readonly HookProc _keyboardProc;

    bool _redirected = false;
    byte _currentButtons = 0;
    readonly List<byte> _heldUsages = new();
    byte _modifiers = 0;
    int _virtualDistance = 0; // net "outward" movement since crossing, for auto-return

    int? _lastTwoFingerY = null; // for two-finger scroll delta tracking

    readonly ScreenEdge _edge;
    readonly int _returnHotkeyVk;
    readonly bool _autoReturnEnabled;

    // Corner calibration - user-marked corners define a precise edge
    // boundary instead of the raw screenWidth/height fallback.
    bool _calibrating = false;
    int _calibrationStep = 0; // 0=TopLeft, 1=TopRight, 2=BottomRight, 3=BottomLeft
    readonly POINT[] _corners = new POINT[4];
    bool _calibrated = false;
    static readonly string[] CornerNames = { "TOP-LEFT", "TOP-RIGHT", "BOTTOM-RIGHT", "BOTTOM-LEFT" };
    CalibrationOverlayForm? _calibrationOverlay;

    NotifyIcon? _trayIcon;
    System.Windows.Forms.Timer? _disconnectPollTimer;
    readonly HashSet<GattSession> _monitoredSessions = new();

    static readonly Dictionary<int, byte> VkToHidUsage = BuildVkMap();

    public InputBridgeForm(GattLocalCharacteristic keyboardReport, GattLocalCharacteristic mouseReport, ScreenEdge edge, int returnHotkeyVk, bool autoReturnEnabled)
    {
        _keyboardReport = keyboardReport;
        _mouseReport = mouseReport;
        _edge = edge;
        _returnHotkeyVk = returnHotkeyVk;
        _autoReturnEnabled = autoReturnEnabled;

        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        Opacity = 0;
        Load += (_, _) => { Hide(); SetupTrayIcon(); StartCalibration(); };

        _mouseProc = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;

        // If the iPad disconnects while we're redirected, there'd be no way
        // to get local mouse/keyboard control back (everything's suppressed
        // waiting for a hotkey/auto-return that will never come from a dead
        // link). Three independent checks, since none of them are fully
        // guaranteed to fire on an abrupt/ungraceful disconnect on their own:
        // subscription-change events, GATT session status events, and a
        // periodic poll that doesn't depend on any event firing at all.
        _keyboardReport.SubscribedClientsChanged += (_, _) => RehookSessionsAsync();
        _mouseReport.SubscribedClientsChanged += (_, _) => RehookSessionsAsync();
    }

    void RehookSessionsAsync()
    {
        if (!IsHandleCreated) return;
        try { BeginInvoke(new Action(RehookSessions)); }
        catch (ObjectDisposedException) { /* form is closing, nothing to do */ }
        catch (InvalidOperationException) { /* handle not ready yet */ }
    }

    void RehookSessions()
    {
        foreach (var client in _mouseReport.SubscribedClients)
        {
            if (_monitoredSessions.Add(client.Session))
                client.Session.SessionStatusChanged += (sender, _) => OnSessionStatusChangedAsync(sender);
        }
        foreach (var client in _keyboardReport.SubscribedClients)
        {
            if (_monitoredSessions.Add(client.Session))
                client.Session.SessionStatusChanged += (sender, _) => OnSessionStatusChangedAsync(sender);
        }
        CheckForDisconnect();
    }

    void OnSessionStatusChangedAsync(GattSession sender)
    {
        if (!IsHandleCreated) return;
        try
        {
            BeginInvoke(new Action(() =>
            {
                Logger.Log($"[GattSession status changed: {sender.SessionStatus}]");
                CheckForDisconnect();
            }));
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    void CheckForDisconnect()
    {
        bool anySubscribed = _mouseReport.SubscribedClients.Count > 0 || _keyboardReport.SubscribedClients.Count > 0;
        if (!anySubscribed && _redirected)
        {
            Logger.Log("[BLE disconnect detected while redirected - returning control to Windows]");
            ReturnToWindows();
            _trayIcon?.ShowBalloonTip(4000, "iPad Bridge",
                "iPad disconnected - control returned to Windows.", ToolTipIcon.Warning);
        }
    }

    void SetupTrayIcon()
    {
        var menu = new ContextMenuStrip();

        var statusItem = new ToolStripMenuItem("Status: local control") { Enabled = false };
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());

        var calibrateItem = new ToolStripMenuItem("Calibrate margins...");
        calibrateItem.Click += (_, _) => StartCalibration();
        menu.Items.Add(calibrateItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => Close();
        menu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "iPad Bridge",
            Visible = true,
            ContextMenuStrip = menu
        };

        // Keep the status line current without needing the menu re-opened.
        menu.Opening += (_, _) =>
        {
            statusItem.Text = _calibrating
                ? $"Calibrating - click the {CornerNames[_calibrationStep]} corner"
                : $"Status: {(_redirected ? "on iPad" : "local control")}"
                  + (_calibrated ? "" : " (not calibrated)");
        };

        // Third detection layer - doesn't depend on any BLE event firing at
        // all, just periodically checks whether anyone is still subscribed.
        _disconnectPollTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _disconnectPollTimer.Tick += (_, _) => { if (_redirected) CheckForDisconnect(); };
        _disconnectPollTimer.Start();
    }

    void StartCalibration()
    {
        if (_redirected) ReturnToWindows(); // don't calibrate while handed off
        _calibrating = true;
        _calibrationStep = 0;
        Logger.Log("[calibration started]");

        _calibrationOverlay?.Close();
        _calibrationOverlay = new CalibrationOverlayForm { Corners = _corners, MarkedCount = 0 };
        _calibrationOverlay.UpdateStep(0, CornerNames[0]);
        _calibrationOverlay.Show();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        var rid = new RAWINPUTDEVICE[2];
        rid[0].usUsagePage = 0x01; // Generic Desktop Controls
        rid[0].usUsage = 0x02;     // Mouse
        rid[0].dwFlags = RIDEV_INPUTSINK;
        rid[0].hwndTarget = Handle;
        rid[1].usUsagePage = 0x0D; // Digitizer
        rid[1].usUsage = 0x05;     // Touch Pad
        // The reference sample this is adapted from uses dwFlags=0 (foreground
        // only), but our window is intentionally hidden/backgrounded, so we
        // need RIDEV_INPUTSINK here too - matches what already works for mouse.
        rid[1].dwFlags = RIDEV_INPUTSINK;
        rid[1].hwndTarget = Handle;
        bool registered = RegisterRawInputDevices(rid, 2, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        Logger.Log($"[raw input devices registered: {registered}]");

        _mouseHookId = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);
        _keyboardHookId = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, GetModuleHandle(null), 0);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_mouseHookId != IntPtr.Zero) UnhookWindowsHookEx(_mouseHookId);
        if (_keyboardHookId != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHookId);
        if (_trayIcon is not null) { _trayIcon.Visible = false; _trayIcon.Dispose(); }
        _disconnectPollTimer?.Stop();
        _disconnectPollTimer?.Dispose();
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INPUT)
        {
            HandleRawInput(m.LParam);
        }
        base.WndProc(ref m);
    }

    void HandleRawInput(IntPtr hRawInput)
    {
        if (!_redirected) return;

        uint size = 0;
        GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
        if (size == 0) return;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRawInput, RID_INPUT, buffer, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>()) != size)
                return;

            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);

            if (header.dwType == RIM_TYPEMOUSE)
            {
                HandleRawMouse(buffer);
            }
            else if (header.dwType == RIM_TYPEHID)
            {
                HandleRawTouchpad(buffer, size, header.hDevice);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    void HandleRawMouse(IntPtr buffer)
    {
        var raw = Marshal.PtrToStructure<RAWINPUT>(buffer);

        var flags = raw.mouse.usButtonFlags;
        if ((flags & RI_MOUSE_LEFT_BUTTON_DOWN) != 0) _currentButtons |= 0x01;
        if ((flags & RI_MOUSE_LEFT_BUTTON_UP) != 0) _currentButtons &= unchecked((byte)~0x01);
        if ((flags & RI_MOUSE_RIGHT_BUTTON_DOWN) != 0) _currentButtons |= 0x02;
        if ((flags & RI_MOUSE_RIGHT_BUTTON_UP) != 0) _currentButtons &= unchecked((byte)~0x02);
        if ((flags & RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0) _currentButtons |= 0x04;
        if ((flags & RI_MOUSE_MIDDLE_BUTTON_UP) != 0) _currentButtons &= unchecked((byte)~0x04);

        sbyte dx = (sbyte)Math.Clamp(raw.mouse.lLastX, -127, 127);
        sbyte dy = (sbyte)Math.Clamp(raw.mouse.lLastY, -127, 127);

        if (_autoReturnEnabled && (dx != 0 || dy != 0))
        {
            // Project the movement onto the "away from the crossing point" axis
            // for whichever edge we used, so this works the same for any of the
            // four. Moving further away increases the tracked distance; moving
            // back decreases it. We never hear from the iPad directly - this is
            // inferred purely from what we've already sent it.
            int outward = _edge switch
            {
                ScreenEdge.Right => dx,
                ScreenEdge.Left => -dx,
                ScreenEdge.Bottom => dy,
                ScreenEdge.Top => -dy,
                _ => 0
            };
            _virtualDistance += outward;

            if (_virtualDistance < -AutoReturnThreshold)
            {
                Logger.Log("[auto-return threshold crossed]");
                ReturnToWindows();
                return;
            }
        }

        if (dx != 0 || dy != 0 || flags != 0)
        {
            _ = HidHelpers.SendMouseReport(_mouseReport, _currentButtons, dx, dy);
        }
    }

    static bool _loggedFirstHid = false;
    static uint _lastLoggedContactCount = uint.MaxValue; // sentinel so the first real report always logs
    static int _lastLoggedTripletCount = -1;

    void HandleRawTouchpad(IntPtr buffer, uint totalSize, IntPtr hDevice)
    {
        if (!_loggedFirstHid)
        {
            _loggedFirstHid = true;
            Logger.Log("[first RIM_TYPEHID raw input received - touchpad HID data is arriving]");
        }

        var rawHidHeader = Marshal.PtrToStructure<RAWINPUT_HID>(buffer);
        int hidDataLength = (int)(rawHidHeader.hid.dwSizeHid * rawHidHeader.hid.dwCount);
        int hidDataOffset = (int)totalSize - hidDataLength;
        if (hidDataLength <= 0 || hidDataOffset < 0)
        {
            Logger.Log($"[touchpad HID: bad data length={hidDataLength} offset={hidDataOffset}, dropping]");
            return;
        }

        byte[] rawHidBytes = new byte[hidDataLength];
        Marshal.Copy(buffer + hidDataOffset, rawHidBytes, 0, hidDataLength);

        var contacts = ParseTouchpadContacts(hDevice, rawHidBytes);
        if (contacts is null) return; // ParseTouchpadContacts logs its own failure reason

        ProcessTouchpadContacts(contacts);
    }

    // Adapted from emoacht/RawInput.Touchpad (TouchpadHelper.cs), a working
    // reference sample for reading Windows Precision Touchpad raw HID input.
    List<(int ContactId, int X, int Y)>? ParseTouchpadContacts(IntPtr hDevice, byte[] rawHidBytes)
    {
        IntPtr reportBuffer = Marshal.AllocHGlobal(rawHidBytes.Length);
        IntPtr preparsedData = IntPtr.Zero;
        try
        {
            Marshal.Copy(rawHidBytes, 0, reportBuffer, rawHidBytes.Length);

            uint preparsedSize = 0;
            if (GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref preparsedSize) != 0)
            {
                Logger.Log("[touchpad parse: failed getting preparsed data size]");
                return null;
            }
            preparsedData = Marshal.AllocHGlobal((int)preparsedSize);
            if (GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, preparsedData, ref preparsedSize) != preparsedSize)
            {
                Logger.Log("[touchpad parse: failed getting preparsed data]");
                return null;
            }

            if (HidP_GetCaps(preparsedData, out HIDP_CAPS caps) != HIDP_STATUS_SUCCESS)
            {
                Logger.Log("[touchpad parse: HidP_GetCaps failed]");
                return null;
            }

            ushort valueCapsLength = caps.NumberInputValueCaps;
            var valueCaps = new HIDP_VALUE_CAPS[valueCapsLength];
            if (HidP_GetValueCaps(HIDP_REPORT_TYPE.HidP_Input, valueCaps, ref valueCapsLength, preparsedData) != HIDP_STATUS_SUCCESS)
            {
                Logger.Log($"[touchpad parse: HidP_GetValueCaps failed (expected {valueCapsLength} value caps)]");
                return null;
            }

            uint contactCount = 0;
            int? curId = null, curX = null, curY = null;
            var allContacts = new List<(int, int, int)>();

            foreach (var vc in valueCaps.OrderBy(v => v.LinkCollection))
            {
                if (HidP_GetUsageValue(HIDP_REPORT_TYPE.HidP_Input, vc.UsagePage, vc.LinkCollection, vc.Usage,
                        out uint value, preparsedData, reportBuffer, (uint)rawHidBytes.Length) != HIDP_STATUS_SUCCESS)
                    continue;

                if (vc.LinkCollection == 0)
                {
                    if (vc.UsagePage == 0x0D && vc.Usage == 0x54) contactCount = value; // Contact Count
                }
                else
                {
                    if (vc.UsagePage == 0x0D && vc.Usage == 0x51) curId = (int)value;      // Contact ID
                    else if (vc.UsagePage == 0x01 && vc.Usage == 0x30) curX = (int)value;  // X
                    else if (vc.UsagePage == 0x01 && vc.Usage == 0x31) curY = (int)value;  // Y
                }

                if (curId.HasValue && curX.HasValue && curY.HasValue)
                {
                    allContacts.Add((curId.Value, curX.Value, curY.Value));
                    curId = curX = curY = null;
                    // No early break here - a previous version stopped as soon as
                    // allContacts.Count reached contactCount, but if contactCount
                    // wasn't read correctly (stays 0), that stops after just the
                    // first contact regardless of how many fingers are actually
                    // down. Collect everything the descriptor offers and decide
                    // which are real below instead.
                }
            }

            if (contactCount != _lastLoggedContactCount || allContacts.Count != _lastLoggedTripletCount)
            {
                _lastLoggedContactCount = contactCount;
                _lastLoggedTripletCount = allContacts.Count;
                Logger.Log($"[touchpad: raw contactCount={contactCount}, triplets found={allContacts.Count}]");
            }

            // Trust contactCount only if it looks sane; otherwise fall back to
            // everything the descriptor gave us for this report.
            var contacts = (contactCount > 0 && contactCount <= allContacts.Count)
                ? allContacts.Take((int)contactCount).ToList()
                : allContacts;

            return contacts;
        }
        finally
        {
            Marshal.FreeHGlobal(reportBuffer);
            if (preparsedData != IntPtr.Zero) Marshal.FreeHGlobal(preparsedData);
        }
    }

    double _scrollAccumulator = 0;

    void ProcessTouchpadContacts(List<(int ContactId, int X, int Y)> contacts)
    {
        if (contacts.Count == 2 && _redirected)
        {
            int avgY = (contacts[0].Y + contacts[1].Y) / 2;

            if (_lastTwoFingerY.HasValue)
            {
                int deltaY = avgY - _lastTwoFingerY.Value;
                if (deltaY != 0)
                {
                    // Accumulate in floating point so small per-frame deltas below
                    // ScrollScaleDivisor build up over time instead of being lost
                    // to integer truncation (the previous version divided and
                    // discarded the remainder every frame - if your touchpad's
                    // per-frame Y movement is smaller than the divisor, that
                    // silently zeroed out every single scroll event).
                    // Sign here is a guess; flip if it feels backwards.
                    _scrollAccumulator += -deltaY / (double)ScrollScaleDivisor;
                    int wholeSteps = (int)_scrollAccumulator;
                    if (wholeSteps != 0)
                    {
                        _scrollAccumulator -= wholeSteps;
                        sbyte wheel = (sbyte)Math.Clamp(wholeSteps, -127, 127);
                        Logger.Log($"[2-finger scroll: deltaY={deltaY} accumulator={_scrollAccumulator:F2} -> wheel={wheel}]");
                        _ = HidHelpers.SendMouseReport(_mouseReport, _currentButtons, 0, 0, wheel);
                    }
                }
            }
            _lastTwoFingerY = avgY;
        }
        else
        {
            _lastTwoFingerY = null;
            _scrollAccumulator = 0; // reset so a later gesture doesn't inherit stale carry
        }
    }

    bool IsAtEdge(POINT pt, int screenWidth, int screenHeight)
    {
        if (_calibrated)
        {
            // corners[0]=TopLeft, [1]=TopRight, [2]=BottomRight, [3]=BottomLeft
            return _edge switch
            {
                ScreenEdge.Right => pt.x >= Math.Min(_corners[1].x, _corners[2].x),
                ScreenEdge.Left => pt.x <= Math.Max(_corners[0].x, _corners[3].x),
                ScreenEdge.Top => pt.y <= Math.Max(_corners[0].y, _corners[1].y),
                ScreenEdge.Bottom => pt.y >= Math.Min(_corners[2].y, _corners[3].y),
                _ => false
            };
        }

        return _edge switch
        {
            ScreenEdge.Right => pt.x >= screenWidth - 2,
            ScreenEdge.Left => pt.x <= 1,
            ScreenEdge.Top => pt.y <= 1,
            ScreenEdge.Bottom => pt.y >= screenHeight - 2,
            _ => false
        };
    }

    void HandleCalibrationClick(POINT pt)
    {
        _corners[_calibrationStep] = pt;
        Logger.Log($"[calibration: {CornerNames[_calibrationStep]} marked at ({pt.x},{pt.y})]");

        _calibrationStep++;
        if (_calibrationOverlay is not null)
            _calibrationOverlay.MarkedCount = _calibrationStep;

        if (_calibrationStep >= 4)
        {
            _calibrating = false;
            _calibrated = true;
            Logger.Log("[calibration complete]");

            _calibrationOverlay?.ShowComplete();
            var closeTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            closeTimer.Tick += (_, _) =>
            {
                closeTimer.Stop();
                closeTimer.Dispose();
                _calibrationOverlay?.Close();
                _calibrationOverlay = null;
            };
            closeTimer.Start();
        }
        else
        {
            _calibrationOverlay?.UpdateStep(_calibrationStep, CornerNames[_calibrationStep]);
        }
    }

    void ReturnToWindows()
    {
        Logger.Log("[ReturnToWindows() called]");
        _redirected = false;
        _heldUsages.Clear();
        _modifiers = 0;
        _currentButtons = 0;
        _virtualDistance = 0;
        _lastTwoFingerY = null;
        _ = HidHelpers.SendKeyboardReport(_keyboardReport, 0, Array.Empty<byte>());
        _ = HidHelpers.SendMouseReport(_mouseReport, 0, 0, 0);

        // Nudge the real cursor a little inside the boundary so we don't
        // instantly re-trigger the same edge crossing.
        var bounds = Screen.PrimaryScreen!.Bounds;
        var current = Cursor.Position;
        Cursor.Position = _edge switch
        {
            ScreenEdge.Right => new Point(bounds.Width - 10, current.Y),
            ScreenEdge.Left => new Point(10, current.Y),
            ScreenEdge.Top => new Point(current.X, 10),
            ScreenEdge.Bottom => new Point(current.X, bounds.Height - 10),
            _ => current
        };

        Logger.Log("[back to Windows]");
    }

    IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();

            if (_calibrating)
            {
                if (msg == WM_LBUTTONDOWN)
                {
                    var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    HandleCalibrationClick(hookStruct.pt);
                    return (IntPtr)1; // suppress just the click so it doesn't hit whatever's underneath
                }
                // Let movement through normally - suppressing it would freeze the
                // OS's tracked cursor position, so every corner would end up
                // recording wherever the cursor happened to be when calibration
                // started instead of where you actually moved to.
                return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
            }

            if (!_redirected)
            {
                if (msg == WM_MOUSEMOVE)
                {
                    var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    var bounds = Screen.PrimaryScreen!.Bounds;
                    if (IsAtEdge(hookStruct.pt, bounds.Width, bounds.Height))
                    {
                        _redirected = true;
                        _currentButtons = 0;
                        _virtualDistance = 0;
                        Logger.Log($"[handed off to iPad - press {(Keys)_returnHotkeyVk} to return]");
                        return (IntPtr)1;
                    }
                }
                return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
            }

            // Redirected: handle wheel here (Precision Touchpad-synthesized
            // two-finger scroll arrives as a classic WM_MOUSEWHEEL message,
            // not through Raw Input's wheel data - a real scroll-wheel mouse
            // would need the same treatment, since it's the same message).
            // Everything else from the legacy pipeline is suppressed - actual
            // movement/clicks are handled via Raw Input in HandleRawInput.
            if (msg == WM_MOUSEWHEEL)
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                short rawDelta = (short)(hookStruct.mouseData >> 16);
                int steps = rawDelta / 120; // WHEEL_DELTA per notch
                if (steps == 0) steps = Math.Sign(rawDelta);
                sbyte wheel = (sbyte)Math.Clamp(steps, -127, 127);
                Logger.Log($"[wheel: rawDelta={rawDelta} -> wheel={wheel}]");
                _ = HidHelpers.SendMouseReport(_mouseReport, _currentButtons, 0, 0, wheel);
            }

            return (IntPtr)1;
        }
        return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
    }

    IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int vk = (int)hookStruct.vkCode;
            bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

            // The configured return hotkey only does anything while redirected -
            // otherwise it behaves as a normal key for local use.
            if (vk == _returnHotkeyVk && isDown && _redirected)
            {
                ReturnToWindows();
                return (IntPtr)1;
            }

            if (_redirected)
            {
                if (isDown || isUp)
                {
                    UpdateModifier(vk, isDown);

                    if (VkToHidUsage.TryGetValue(vk, out byte usage))
                    {
                        if (isDown && !_heldUsages.Contains(usage) && _heldUsages.Count < 6)
                            _heldUsages.Add(usage);
                        else if (isUp)
                            _heldUsages.Remove(usage);
                    }

                    _ = HidHelpers.SendKeyboardReport(_keyboardReport, _modifiers, _heldUsages.ToArray());
                }
                return (IntPtr)1; // suppressed locally while redirected
            }
        }
        return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
    }

    void UpdateModifier(int vk, bool isDown)
    {
        byte bit = vk switch
        {
            0xA0 => 0x02, // VK_LSHIFT
            0xA1 => 0x20, // VK_RSHIFT
            0xA2 => 0x01, // VK_LCONTROL
            0xA3 => 0x10, // VK_RCONTROL
            0xA4 => 0x04, // VK_LMENU (Alt)
            0xA5 => 0x40, // VK_RMENU
            0x5B => 0x08, // VK_LWIN
            0x5C => 0x80, // VK_RWIN
            _ => (byte)0
        };
        if (bit == 0) return;
        if (isDown) _modifiers |= bit;
        else _modifiers &= (byte)~bit;
    }

    static Dictionary<int, byte> BuildVkMap()
    {
        var map = new Dictionary<int, byte>();
        for (int c = 'A'; c <= 'Z'; c++)
            map[c] = (byte)(0x04 + (c - 'A'));

        for (int d = '1'; d <= '9'; d++)
            map[d] = (byte)(0x1E + (d - '1'));
        map['0'] = 0x27;

        map[0x0D] = 0x28; // Enter
        map[0x1B] = 0x29; // Escape
        map[0x08] = 0x2A; // Backspace
        map[0x09] = 0x2B; // Tab
        map[0x20] = 0x2C; // Space
        map[0xBD] = 0x2D; // -
        map[0xBB] = 0x2E; // =
        map[0xDB] = 0x2F; // [
        map[0xDD] = 0x30; // ]
        map[0xDC] = 0x31; // backslash
        map[0xBA] = 0x33; // ;
        map[0xDE] = 0x34; // '
        map[0xC0] = 0x35; // `
        map[0xBC] = 0x36; // ,
        map[0xBE] = 0x37; // .
        map[0xBF] = 0x38; // /
        map[0x14] = 0x39; // Caps Lock

        for (int f = 0; f < 12; f++)
            map[0x70 + f] = (byte)(0x3A + f); // F1..F12

        map[0x2D] = 0x49; // Insert
        map[0x2E] = 0x4C; // Delete
        map[0x24] = 0x4A; // Home
        map[0x23] = 0x4D; // End
        map[0x21] = 0x4B; // Page Up
        map[0x22] = 0x4E; // Page Down
        map[0x25] = 0x50; // Left
        map[0x26] = 0x52; // Up
        map[0x27] = 0x4F; // Right
        map[0x28] = 0x51; // Down

        return map;
    }
}
