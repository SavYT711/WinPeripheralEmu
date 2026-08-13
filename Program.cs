using System.Windows.Forms;
using BlePeripheralPoc;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

// ---------------------------------------------------------------------
// BLE HID-over-GATT (HOGP) peripheral - combo keyboard + mouse - plus a
// real trackpad/keyboard passthrough bridge with edge-of-screen handoff.
//
// Settings (edge, return hotkey, auto-return, scroll direction) and the
// four-corner calibration persist to %APPDATA%\iPad Bridge\settings.json,
// so the setup dialog only appears on first run; it's reachable any time
// from the tray icon afterwards.
//
// This build is a real Windows executable (no console) - status lives in
// a system tray icon instead, and all diagnostic output goes to
// debug.log next to the .exe (there's no console to print to anymore).
//
// Known simplification: writes to Protocol Mode aren't persisted back
// (it's created as a static Report-Protocol value). Harmless here - it
// only matters if a host explicitly switches to Boot Protocol, which
// iOS does not.
// ---------------------------------------------------------------------

Logger.Initialize();

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

// Two copies would fight over the low-level hooks and both advertise as
// HID peripherals. Easy to end up with, given the installer offers both a
// desktop shortcut and a run-at-startup entry.
using var singleInstance = new Mutex(true, @"Local\iPadBridge.SingleInstance", out bool isOnlyInstance);
if (!isOnlyInstance)
{
    Logger.Log("Another instance is already running. Exiting.");
    MessageBox.Show(
        "iPad Bridge is already running - look for it in the system tray.",
        "iPad Bridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
    return;
}

var settings = AppSettings.Load();

try
{
    var hidServiceUuid      = BluetoothUuidHelper.FromShortId(0x1812);
    var hidInfoUuid         = BluetoothUuidHelper.FromShortId(0x2A4A);
    var reportMapUuid       = BluetoothUuidHelper.FromShortId(0x2A4B);
    var hidControlPointUuid = BluetoothUuidHelper.FromShortId(0x2A4C);
    var reportUuid          = BluetoothUuidHelper.FromShortId(0x2A4D);
    var protocolModeUuid    = BluetoothUuidHelper.FromShortId(0x2A4E);
    var reportReferenceUuid = BluetoothUuidHelper.FromShortId(0x2908);

    var deviceInfoServiceUuid = BluetoothUuidHelper.FromShortId(0x180A);
    var manufacturerNameUuid  = BluetoothUuidHelper.FromShortId(0x2A29);
    var pnpIdUuid             = BluetoothUuidHelper.FromShortId(0x2A50);

    var batteryServiceUuid = BluetoothUuidHelper.FromShortId(0x180F);
    var batteryLevelUuid   = BluetoothUuidHelper.FromShortId(0x2A19);

    // Combo HID report descriptor: keyboard (Report ID 1) + mouse (Report ID 2).
    // This is standard USB-HID/Bluetooth-SIG descriptor structure, the same
    // shape used by essentially every DIY BLE keyboard/mouse project.
    //
    // Do not change these bytes casually: hosts cache the report map against
    // the bond, so an edit means every paired device has to be unpaired and
    // paired again before it works properly. The mouse report is 5 bytes:
    // [buttons, dx, dy, wheel, pan].
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

        // Horizontal scroll. AC Pan lives on the Consumer page rather than
        // Generic Desktop, hence the separate usage-page item and the 2-byte
        // usage encoding (0x0A) for the 16-bit usage id.
        0x05, 0x0C,       //     Usage Page (Consumer)
        0x0A, 0x38, 0x02, //     Usage (AC Pan)
        0x15, 0x81,       //     Logical Minimum (-127)
        0x25, 0x7F,       //     Logical Maximum (127)
        0x75, 0x08,       //     Report Size (8)
        0x95, 0x01,       //     Report Count (1)
        0x81, 0x06,       //     Input (AC Pan - relative)
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
    else
    {
        Logger.Log($"Device Information service unavailable: {devInfoResult.Error}");
    }

    Logger.Log("Creating Battery service...");
    var batteryResult = await GattServiceProvider.CreateAsync(batteryServiceUuid);
    if (batteryResult.Error == BluetoothError.Success)
    {
        // Reported once at startup rather than hardcoded to 100%. The
        // characteristic holds a static value, so it doesn't track the battery
        // while running - it just stops claiming a full charge on a laptop
        // that's nearly flat.
        byte batteryLevel = 100;
        var power = SystemInformation.PowerStatus;
        if (power.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery &&
            power.BatteryLifePercent is > 0f and <= 1f)
        {
            batteryLevel = (byte)Math.Clamp((int)Math.Round(power.BatteryLifePercent * 100), 1, 100);
        }
        Logger.Log($"Reporting battery level {batteryLevel}%.");

        var batteryWriter = new DataWriter();
        batteryWriter.WriteByte(batteryLevel);
        await HidHelpers.CreateReadCharacteristic(batteryResult.ServiceProvider.Service, batteryLevelUuid, batteryWriter.DetachBuffer());
    }
    else
    {
        Logger.Log($"Battery service unavailable: {batteryResult.Error}");
    }

    hidProvider.AdvertisementStatusChanged += (_, args) =>
        Logger.Log($"Advertisement status: {args.Status}");

    hidProvider.StartAdvertising(new GattServiceProviderAdvertisingParameters
    {
        IsDiscoverable = true,
        IsConnectable = true
    });

    // The setup dialog is only forced the first time. After that the saved
    // settings are used and the dialog lives behind the tray icon.
    if (settings.IsFirstRun)
    {
        Logger.Log("First run - opening settings window...");
        var setup = new SettingsForm(settings, firstRun: true);
        if (setup.ShowDialog() != DialogResult.OK)
        {
            Logger.Log("Cancelled.");
            hidProvider.StopAdvertising();
            return;
        }
    }

    Logger.Log($"Edge: {settings.Edge}, return key: {(Keys)settings.ReturnHotkeyVk}, " +
               $"auto-return: {settings.AutoReturnEnabled} (travel {settings.VirtualTravelCounts})");
    Logger.Log("Right-click the tray icon and choose Exit to quit.");

    Application.Run(new InputBridgeForm(keyboardReportChar, mouseReportChar, settings));

    hidProvider.StopAdvertising();
    Logger.Log("Stopped.");
}
catch (Exception ex)
{
    Logger.Log($"[FATAL] Startup failed: {ex}");
    MessageBox.Show(
        $"iPad Bridge couldn't start:\n\n{ex.Message}\n\nSee debug.log next to the app for details.",
        "iPad Bridge - Can't start", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
