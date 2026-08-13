using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using static BlePeripheralPoc.NativeMethods;

// Windows.Foundation is deliberately not imported: it defines its own Point,
// which collides with System.Drawing.Point used throughout the WinForms code.
using SessionStatusHandler = Windows.Foundation.TypedEventHandler<
    Windows.Devices.Bluetooth.GenericAttributeProfile.GattSession,
    Windows.Devices.Bluetooth.GenericAttributeProfile.GattSessionStatusChangedEventArgs>;

namespace BlePeripheralPoc;

/// <summary>
/// Hidden window hosting the input bridge: global mouse/keyboard hooks for
/// edge detection and suppression, Raw Input for unclamped mouse deltas once
/// redirected, a configurable return-to-Windows hotkey, and auto-return when
/// the tracked pointer comes back across the edge it left from.
/// </summary>
sealed class InputBridgeForm : Form
{
    /// <summary>
    /// The pointer must travel at least this far out before crossing back is
    /// treated as a return. Stops a handoff from bouncing straight back if the
    /// first delta after crossing happens to point inward.
    /// </summary>
    const int ReturnArmDistance = 200;

    /// <summary>Auto-return stays disabled for this long after any scroll activity.</summary>
    const int ScrollBlocksAutoReturnMs = 800;

    /// <summary>
    /// How long raw-HID scroll waits for Windows to synthesise WM_MOUSEWHEEL
    /// before taking over. Prevents both paths emitting for the same gesture.
    /// </summary>
    const int RawScrollStartDelayMs = 120;

    /// <summary>Once the OS has synthesised a wheel event, raw-HID scroll stays out of the way this long.</summary>
    const int OsWheelGraceMs = 2000;

    /// <summary>
    /// Low-pass weight applied to per-frame contact velocity. Contact
    /// coordinates wobble by a unit or two between reports and at ~130
    /// reports/sec that wobble lands on notch boundaries, which reads as
    /// jitter. Lower is smoother but laggier.
    /// </summary>
    const double ScrollSmoothing = 0.35;

    /// <summary>
    /// A frame or two that doesn't report exactly two contacts is treated as a
    /// dropout rather than the end of the gesture. Tearing the gesture down and
    /// restarting it mid-scroll was a visible stutter.
    /// </summary>
    const int ScrollDropoutGraceMs = 150;

    /// <summary>Safety net: calibration suppresses all input, so it can't be allowed to hang forever.</summary>
    const int CalibrationTimeoutMs = 60_000;

    const int VkEscape = 0x1B;

    static readonly string[] CornerNames = { "TOP-LEFT", "TOP-RIGHT", "BOTTOM-RIGHT", "BOTTOM-LEFT" };
    static readonly Dictionary<int, byte> VkToHidUsage = BuildVkMap();

    readonly AppSettings _settings;
    readonly GattLocalCharacteristic _keyboardReport;
    readonly GattLocalCharacteristic _mouseReport;
    readonly MouseReportPump _mousePump;
    readonly KeyboardReportPump _keyboardPump;
    readonly TouchpadParser _touchpadParser = new();

    readonly HookProc _mouseProc;
    readonly HookProc _keyboardProc;
    IntPtr _mouseHookId = IntPtr.Zero;
    IntPtr _keyboardHookId = IntPtr.Zero;

    bool _redirected;
    byte _currentButtons;
    readonly List<byte> _heldUsages = new();

    // Physical keys we swallowed while redirected. Their key-ups must be
    // swallowed too, otherwise Windows sees an up with no matching down the
    // moment control comes back.
    readonly HashSet<int> _heldVks = new();
    readonly HashSet<int> _suppressUpVks = new();
    byte _modifiers;

    // Auto-return: dead-reckoned distance out from the edge we crossed.
    int _virtualOutward;
    bool _returnArmed;
    long _lastScrollTicks;

    // Sub-unit remainders from the sensitivity multiplier, kept so slow
    // movement at low sensitivity isn't truncated away to nothing.
    double _mouseRemainderX, _mouseRemainderY;

    // Scroll state.
    long _lastOsWheelTicks, _lastOsHWheelTicks;
    double _osWheelAccumulator, _osHWheelAccumulator;
    long _twoFingerStartTicks, _lastTwoFingerTicks;
    bool _scrollGestureActive;
    readonly Dictionary<int, TouchContact> _prevContacts = new();
    double _smoothedContactDx, _smoothedContactDy;
    double _rawScrollAccumX, _rawScrollAccumY;

    // Corner calibration - user-marked corners define a precise edge boundary
    // instead of the raw virtual-screen fallback.
    bool _calibrating;
    int _calibrationStep;
    readonly POINT[] _corners = new POINT[4];
    bool _calibrated;
    CalibrationOverlayForm? _calibrationOverlay;
    System.Windows.Forms.Timer? _calibrationTimeoutTimer;

    NotifyIcon? _trayIcon;
    ToolStripMenuItem? _statusItem;
    System.Windows.Forms.Timer? _disconnectPollTimer;
    readonly Dictionary<GattSession, SessionStatusHandler> _sessionHandlers = new();

    public InputBridgeForm(GattLocalCharacteristic keyboardReport, GattLocalCharacteristic mouseReport, AppSettings settings)
    {
        _settings = settings;
        _keyboardReport = keyboardReport;
        _mouseReport = mouseReport;
        _mousePump = new MouseReportPump(mouseReport);
        _keyboardPump = new KeyboardReportPump(keyboardReport);

        if (settings.Calibrated && settings.Corners.Count == 4)
        {
            for (int i = 0; i < 4; i++)
                _corners[i] = new POINT { x = settings.Corners[i].X, y = settings.Corners[i].Y };
            _calibrated = true;
            Logger.Log("[using saved calibration]");
        }

        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        Opacity = 0;
        Load += (_, _) =>
        {
            Hide();
            SetupTrayIcon();
            if (!_calibrated) StartCalibration();
        };

        _mouseProc = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;

        // If the iPad disconnects while we're redirected, there'd be no way to
        // get local mouse/keyboard control back (everything's suppressed
        // waiting for a hotkey/auto-return that will never come from a dead
        // link). Three independent checks, since none of them is guaranteed to
        // fire on an abrupt disconnect on its own: subscription-change events,
        // GATT session status events, and a periodic poll that doesn't depend
        // on any event firing at all.
        _keyboardReport.SubscribedClientsChanged += (_, _) => RehookSessionsAsync();
        _mouseReport.SubscribedClientsChanged += (_, _) => RehookSessionsAsync();
    }

    // --- connection monitoring ---

    void RehookSessionsAsync()
    {
        if (!IsHandleCreated) return;
        try { BeginInvoke(new Action(RehookSessions)); }
        catch (ObjectDisposedException) { /* form is closing, nothing to do */ }
        catch (InvalidOperationException) { /* handle not ready yet */ }
    }

    void RehookSessions()
    {
        foreach (var client in _mouseReport.SubscribedClients) TrackSession(client.Session);
        foreach (var client in _keyboardReport.SubscribedClients) TrackSession(client.Session);
        PruneClosedSessions();
        CheckForDisconnect();
    }

    void TrackSession(GattSession session)
    {
        if (_sessionHandlers.ContainsKey(session)) return;

        SessionStatusHandler handler = (sender, _) => OnSessionStatusChangedAsync(sender);
        session.SessionStatusChanged += handler;
        _sessionHandlers[session] = handler;
    }

    /// <summary>
    /// Detaches handlers for sessions that have gone away. Without this the
    /// handler set grew by one entry per reconnect for the life of the process.
    /// </summary>
    void PruneClosedSessions()
    {
        foreach (var pair in _sessionHandlers.Where(p => p.Key.SessionStatus == GattSessionStatus.Closed).ToList())
        {
            pair.Key.SessionStatusChanged -= pair.Value;
            _sessionHandlers.Remove(pair.Key);
        }
    }

    void OnSessionStatusChangedAsync(GattSession sender)
    {
        if (!IsHandleCreated) return;
        try
        {
            BeginInvoke(new Action(() =>
            {
                Logger.Log($"[GattSession status changed: {sender.SessionStatus}]");
                PruneClosedSessions();
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

    // --- tray ---

    void SetupTrayIcon()
    {
        var menu = new ContextMenuStrip();

        _statusItem = new ToolStripMenuItem("Status: local control") { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());

        var settingsItem = new ToolStripMenuItem("Settings...");
        settingsItem.Click += (_, _) => ShowSettings();
        menu.Items.Add(settingsItem);

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

        menu.Opening += (_, _) => UpdateStatusText();

        // Third detection layer - doesn't depend on any BLE event firing at
        // all, just periodically checks whether anyone is still subscribed.
        _disconnectPollTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _disconnectPollTimer.Tick += (_, _) => { if (_redirected) CheckForDisconnect(); };
        _disconnectPollTimer.Start();
    }

    /// <summary>
    /// Queues a status refresh. Setting NotifyIcon.Text talks to the shell, so
    /// it must not happen inline in a hook callback.
    /// </summary>
    void UpdateStatusTextAsync()
    {
        if (!IsHandleCreated) return;
        try { BeginInvoke(new Action(UpdateStatusText)); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    void UpdateStatusText()
    {
        string status = _calibrating
            ? $"Calibrating - click the {CornerNames[_calibrationStep]} corner"
            : $"Status: {(_redirected ? "on iPad" : "local control")}" + (_calibrated ? "" : " (not calibrated)");

        if (_statusItem is not null) _statusItem.Text = status;
        // NotifyIcon.Text is capped at 63 characters by the shell.
        if (_trayIcon is not null)
            _trayIcon.Text = status.Length > 62 ? status[..62] : status;
    }

    void ShowSettings()
    {
        if (_redirected) ReturnToWindows();
        if (_calibrating) CancelCalibration();

        using var dialog = new SettingsForm(_settings, firstRun: false);
        dialog.ShowDialog();
        UpdateStatusText();
    }

    // --- calibration ---

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

        RestartCalibrationTimeout();
    }

    void RestartCalibrationTimeout()
    {
        StopCalibrationTimeout();
        _calibrationTimeoutTimer = new System.Windows.Forms.Timer { Interval = CalibrationTimeoutMs };
        _calibrationTimeoutTimer.Tick += (_, _) =>
        {
            Logger.Log("[calibration timed out - releasing input]");
            CancelCalibration();
        };
        _calibrationTimeoutTimer.Start();
    }

    void StopCalibrationTimeout()
    {
        _calibrationTimeoutTimer?.Stop();
        _calibrationTimeoutTimer?.Dispose();
        _calibrationTimeoutTimer = null;
    }

    void CancelCalibration()
    {
        if (!_calibrating) return;

        _calibrating = false;
        StopCalibrationTimeout();
        Logger.Log("[calibration cancelled]");

        _calibrationOverlay?.ShowCancelled();
        CloseOverlayShortly();
    }

    /// <summary>
    /// Runs inside the low-level mouse hook, so it does the bare minimum and
    /// hands the rest off. A hook callback that blocks for longer than
    /// LowLevelHooksTimeout (300ms by default) is silently uninstalled by
    /// Windows - which showing a dialog or writing a file from here would do.
    /// </summary>
    void HandleCalibrationClick(POINT pt)
    {
        if (_calibrationStep >= 4) return;

        _corners[_calibrationStep] = pt;
        int step = _calibrationStep++;

        // Stop swallowing input the instant the last corner lands, rather than
        // whenever the deferred work gets around to running.
        if (_calibrationStep >= 4) _calibrating = false;

        try { BeginInvoke(new Action(() => OnCornerMarked(step))); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    void OnCornerMarked(int step)
    {
        var pt = _corners[step];
        Logger.Log($"[calibration: {CornerNames[step]} marked at ({pt.x},{pt.y})]");

        if (_calibrationOverlay is not null)
            _calibrationOverlay.MarkedCount = step + 1;

        if (step < 3)
        {
            _calibrationOverlay?.UpdateStep(step + 1, CornerNames[step + 1]);
            RestartCalibrationTimeout();
            return;
        }

        StopCalibrationTimeout();

        if (!CalibrationLooksUsable())
        {
            Logger.Log("[calibration rejected - corners span too small an area]");
            _calibrationOverlay?.ShowCancelled();
            CloseOverlayShortly();
            MessageBox.Show(
                "Those four points are too close together to describe the screen.\n\n" +
                "Calibration was discarded - the app will use the screen edges instead. " +
                "You can try again from the tray icon.",
                "iPad Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _calibrated = true;
        _settings.Calibrated = true;
        _settings.Corners = _corners.Select(c => new SavedPoint { X = c.x, Y = c.y }).ToList();
        _settings.Save();
        Logger.Log("[calibration complete and saved]");

        _calibrationOverlay?.ShowComplete();
        CloseOverlayShortly();
    }

    bool CalibrationLooksUsable()
    {
        int minX = _corners.Min(c => c.x), maxX = _corners.Max(c => c.x);
        int minY = _corners.Min(c => c.y), maxY = _corners.Max(c => c.y);
        return maxX - minX >= 200 && maxY - minY >= 200;
    }

    void CloseOverlayShortly()
    {
        // Captured rather than read at tick time: starting a new calibration in
        // the meantime would otherwise leave this timer closing the new overlay.
        var overlay = _calibrationOverlay;
        if (overlay is null) return;

        var closeTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            closeTimer.Dispose();
            overlay.Close();
            overlay.Dispose();
            if (ReferenceEquals(_calibrationOverlay, overlay)) _calibrationOverlay = null;
        };
        closeTimer.Start();
    }

    // --- window plumbing ---

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        var rid = new RAWINPUTDEVICE[2];
        rid[0].usUsagePage = UsagePageGenericDesktop;
        rid[0].usUsage = UsageMouse;
        rid[0].dwFlags = RIDEV_INPUTSINK;
        rid[0].hwndTarget = Handle;
        rid[1].usUsagePage = UsagePageDigitizer;
        rid[1].usUsage = UsageTouchPad;
        // The reference sample this is adapted from uses dwFlags=0 (foreground
        // only), but our window is intentionally hidden/backgrounded, so we
        // need RIDEV_INPUTSINK here too - matches what already works for mouse.
        rid[1].dwFlags = RIDEV_INPUTSINK;
        rid[1].hwndTarget = Handle;

        bool registered = RegisterRawInputDevices(rid, 2, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        Logger.Log(registered
            ? "[raw input devices registered]"
            : $"[raw input registration FAILED: win32 error {Marshal.GetLastWin32Error()}]");

        _mouseHookId = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);
        _keyboardHookId = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, GetModuleHandle(null), 0);
        if (_mouseHookId == IntPtr.Zero || _keyboardHookId == IntPtr.Zero)
            Logger.Log($"[hook installation FAILED: win32 error {Marshal.GetLastWin32Error()}]");
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Release everything on the host before the link goes away, otherwise
        // a key or button held at exit stays stuck down on the iPad.
        if (_redirected)
        {
            _keyboardPump.Post(0, Array.Empty<byte>());
            _mousePump.Post(0, 0, 0, 0, 0);
            _keyboardPump.Flush(300);
            _mousePump.Flush(300);
        }

        if (_mouseHookId != IntPtr.Zero) UnhookWindowsHookEx(_mouseHookId);
        if (_keyboardHookId != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHookId);

        var rid = new RAWINPUTDEVICE[2];
        rid[0].usUsagePage = UsagePageGenericDesktop;
        rid[0].usUsage = UsageMouse;
        rid[0].dwFlags = RIDEV_REMOVE;
        rid[1].usUsagePage = UsagePageDigitizer;
        rid[1].usUsage = UsageTouchPad;
        rid[1].dwFlags = RIDEV_REMOVE;
        RegisterRawInputDevices(rid, 2, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());

        foreach (var pair in _sessionHandlers) pair.Key.SessionStatusChanged -= pair.Value;
        _sessionHandlers.Clear();

        if (_trayIcon is not null) { _trayIcon.Visible = false; _trayIcon.Dispose(); }
        _disconnectPollTimer?.Stop();
        _disconnectPollTimer?.Dispose();
        StopCalibrationTimeout();
        _calibrationOverlay?.Close();
        _calibrationOverlay?.Dispose();

        _mousePump.Dispose();
        _keyboardPump.Dispose();
        _touchpadParser.Dispose();

        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INPUT) HandleRawInput(m.LParam);
        base.WndProc(ref m);
    }

    // --- raw input ---

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
            if (header.dwType == RIM_TYPEMOUSE) HandleRawMouse(buffer);
            else if (header.dwType == RIM_TYPEHID) HandleRawTouchpad(buffer, size, header.hDevice);
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

        // Absolute-mode devices (tablets, RDP, some VMs) report screen
        // positions rather than deltas; feeding those in as relative motion
        // would fling the iPad pointer across the screen.
        if ((raw.mouse.usFlags & MOUSE_MOVE_ABSOLUTE) != 0)
        {
            if (flags != 0) _mousePump.Post(_currentButtons, 0, 0, 0, 0);
            return;
        }

        int rawDx = raw.mouse.lLastX;
        int rawDy = raw.mouse.lLastY;

        // Wheel deliberately not read from raw input here: Windows also
        // delivers it as WM_MOUSEWHEEL to the low-level hook, and handling
        // both double-counts every notch.

        // Tracked on the unscaled deltas, so the travel distance means the same
        // amount of physical hand movement at any pointer-speed setting.
        if (rawDx != 0 || rawDy != 0) TrackVirtualPosition(rawDx, rawDy);

        // TrackVirtualPosition may have just handed control back, in which case
        // movement belongs to Windows and must not also be sent to the iPad.
        if (!_redirected) return;

        int dx = ApplySensitivity(rawDx, ref _mouseRemainderX);
        int dy = ApplySensitivity(rawDy, ref _mouseRemainderY);

        // Deltas are passed through whole - the pump splits anything beyond
        // the HID field's +/-127 across several reports. Clamping here used to
        // silently discard the excess, so fast swipes came up short.
        if (dx != 0 || dy != 0 || flags != 0)
            _mousePump.Post(_currentButtons, dx, dy, 0, 0);
    }

    /// <summary>
    /// Scales one axis by the sensitivity setting, carrying the fractional part
    /// forward. Without the carry, slow movement below the multiplier's
    /// resolution would truncate to zero and the pointer would refuse to creep.
    /// </summary>
    int ApplySensitivity(int delta, ref double remainder)
    {
        if (delta == 0 && remainder == 0) return 0;

        double scaled = delta * _settings.MouseSensitivity + remainder;
        int whole = (int)scaled;
        remainder = scaled - whole;
        return whole;
    }

    /// <summary>
    /// Tracks how far out from the crossed edge the pointer has travelled, and
    /// hands control back when it comes home past that edge - the same boundary
    /// crossing that triggered the handoff, just in reverse.
    ///
    /// The position is dead-reckoned: the iPad never tells us where its pointer
    /// actually is, so this is inferred purely from the deltas already sent.
    /// It's clamped at both ends. The floor is what makes the return a margin
    /// crossing rather than a distance travelled, and the ceiling stops a long
    /// excursion across the iPad from demanding an equally long journey back.
    /// </summary>
    void TrackVirtualPosition(int dx, int dy)
    {
        if (!_settings.AutoReturnEnabled) return;

        // Two-finger scrolling produces pointer motion on some touchpads, and
        // it shouldn't move the tracked position at all.
        if (Environment.TickCount64 - _lastScrollTicks < ScrollBlocksAutoReturnMs) return;

        // Project movement onto the "away from the crossing point" axis for
        // whichever edge is in use, so this works the same for all four.
        int outward = _settings.Edge switch
        {
            ScreenEdge.Right => dx,
            ScreenEdge.Left => -dx,
            ScreenEdge.Bottom => dy,
            ScreenEdge.Top => -dy,
            _ => 0
        };
        if (outward == 0) return;

        _virtualOutward = Math.Clamp(_virtualOutward + outward, 0, _settings.TravelCounts);

        if (!_returnArmed)
        {
            if (_virtualOutward >= ReturnArmDistance) _returnArmed = true;
            return;
        }

        if (_virtualOutward <= 0)
        {
            Logger.Log("[auto-return: pointer came back past the edge]");
            ReturnToWindows();
        }
    }

    void HandleRawTouchpad(IntPtr buffer, uint totalSize, IntPtr hDevice)
    {
        var rawHidHeader = Marshal.PtrToStructure<RAWINPUT_HID>(buffer);
        int reportSize = (int)rawHidHeader.hid.dwSizeHid;
        int reportCount = (int)rawHidHeader.hid.dwCount;
        if (reportSize <= 0 || reportCount <= 0) return;

        int hidDataLength = reportSize * reportCount;
        int hidDataOffset = (int)totalSize - hidDataLength;
        if (hidDataOffset < 0)
        {
            Logger.Log($"[touchpad HID: bad data length={hidDataLength} offset={hidDataOffset}, dropping]");
            return;
        }

        byte[] rawHidBytes = new byte[hidDataLength];
        Marshal.Copy(buffer + hidDataOffset, rawHidBytes, 0, hidDataLength);

        // A single WM_INPUT can carry several coalesced reports, and a touch
        // frame can equally span several WM_INPUTs. The parser assembles frames
        // across both, and returns null until one is complete.
        for (int i = 0; i < reportCount; i++)
        {
            var contacts = _touchpadParser.Parse(hDevice, rawHidBytes, i * reportSize, reportSize);
            if (contacts is not null)
                ProcessTouchpadContacts(contacts, _touchpadParser.ScrollDivisor);
        }
    }

    /// <summary>
    /// Fallback two-finger scroll, for touchpads where Windows doesn't
    /// synthesise WM_MOUSEWHEEL from the gesture. Stays dormant whenever the
    /// OS wheel path is working so a single gesture can't scroll twice.
    /// </summary>
    void ProcessTouchpadContacts(List<TouchContact> contacts, double divisor)
    {
        long now = Environment.TickCount64;

        // Any multi-finger gesture holds auto-return off, even a three-finger
        // one that isn't a scroll - none of them should read as a swipe back
        // toward the edge.
        if (contacts.Count >= 2) _lastScrollTicks = now;

        // Scroll is a strictly two-finger gesture, but a frame that briefly
        // reports one contact mid-swipe is a dropout, not the end of it.
        if (contacts.Count != 2 || !_redirected)
        {
            bool transientDropout = _scrollGestureActive
                                    && contacts.Count != 0
                                    && now - _lastTwoFingerTicks < ScrollDropoutGraceMs;
            if (!transientDropout) EndScrollGesture();
            return;
        }

        _lastTwoFingerTicks = now;

        if (!_scrollGestureActive)
        {
            _scrollGestureActive = true;
            _twoFingerStartTicks = now;
            _smoothedContactDx = _smoothedContactDy = 0;
            _rawScrollAccumX = _rawScrollAccumY = 0;
            RememberContacts(contacts);
            return;
        }

        // Average the movement of the fingers present in both this frame and
        // the last, matched by contact id. Averaging by position instead meant
        // that two fingers swapping slots between frames registered as a large
        // jump - a big part of what made this feel jittery.
        int sumDx = 0, sumDy = 0, matched = 0;
        foreach (var c in contacts)
        {
            if (!_prevContacts.TryGetValue(c.Id, out var previous)) continue;
            sumDx += c.X - previous.X;
            sumDy += c.Y - previous.Y;
            matched++;
        }
        RememberContacts(contacts);
        if (matched == 0) return;

        _smoothedContactDx += (sumDx / (double)matched - _smoothedContactDx) * ScrollSmoothing;
        _smoothedContactDy += (sumDy / (double)matched - _smoothedContactDy) * ScrollSmoothing;

        if (now - _twoFingerStartTicks < RawScrollStartDelayMs) return;

        // Signs differ per axis and that's correct, not a typo. Touchpad Y grows
        // downward while "scroll down" is a negative wheel delta, so vertical
        // inverts; touchpad X grows rightward and "pan right" is a positive AC
        // Pan value, so horizontal doesn't. This matches the sign Windows uses
        // in WM_MOUSEWHEEL / WM_MOUSEHWHEEL, keeping both scroll paths
        // consistent. InvertScroll flips both.
        double scale = Math.Max(1.0, divisor) / _settings.ScrollSpeed;
        if (now - _lastOsWheelTicks >= OsWheelGraceMs) _rawScrollAccumY += -_smoothedContactDy / scale;
        if (now - _lastOsHWheelTicks >= OsWheelGraceMs) _rawScrollAccumX += _smoothedContactDx / scale;

        int notchesY = (int)_rawScrollAccumY;
        int notchesX = (int)_rawScrollAccumX;
        if (notchesY == 0 && notchesX == 0) return;

        _rawScrollAccumY -= notchesY;
        _rawScrollAccumX -= notchesX;
        PostWheel(notchesY, notchesX, "raw-hid");
    }

    void RememberContacts(List<TouchContact> contacts)
    {
        _prevContacts.Clear();
        foreach (var c in contacts) _prevContacts[c.Id] = c;
    }

    void EndScrollGesture()
    {
        _scrollGestureActive = false;
        _twoFingerStartTicks = 0;
        _prevContacts.Clear();
        _smoothedContactDx = _smoothedContactDy = 0;
        _rawScrollAccumX = _rawScrollAccumY = 0; // no stale carry into the next gesture
    }

    long _lastScrollLogTicks;

    void PostWheel(int wheel, int pan, string source)
    {
        if (_settings.InvertScroll)
        {
            wheel = -wheel;
            pan = -pan;
        }

        long now = Environment.TickCount64;
        _lastScrollTicks = now;

        // Throttled: this runs inside the low-level mouse hook for the
        // WM_MOUSEWHEEL path, and a file write per notch during fast scrolling
        // is exactly the kind of delay that gets a hook uninstalled.
        if (now - _lastScrollLogTicks > 500)
        {
            _lastScrollLogTicks = now;
            Logger.Log($"[scroll via {source}: wheel={wheel} pan={pan}]");
        }

        _mousePump.Post(_currentButtons, 0, 0, wheel, pan);
    }

    // --- edge geometry ---

    /// <summary>
    /// The coordinate that counts as "at the edge" - x for Left/Right, y for
    /// Top/Bottom. Uses calibrated corners when available, otherwise the outer
    /// boundary of the whole virtual desktop, so that moving between monitors
    /// doesn't trigger a handoff.
    /// </summary>
    int EdgeBoundary()
    {
        if (_calibrated)
        {
            // corners[0]=TopLeft, [1]=TopRight, [2]=BottomRight, [3]=BottomLeft
            return _settings.Edge switch
            {
                ScreenEdge.Right => Math.Min(_corners[1].x, _corners[2].x),
                ScreenEdge.Left => Math.Max(_corners[0].x, _corners[3].x),
                ScreenEdge.Top => Math.Max(_corners[0].y, _corners[1].y),
                ScreenEdge.Bottom => Math.Min(_corners[2].y, _corners[3].y),
                _ => 0
            };
        }

        var bounds = SystemInformation.VirtualScreen;
        return _settings.Edge switch
        {
            ScreenEdge.Right => bounds.Right - 2,
            ScreenEdge.Left => bounds.Left + 1,
            ScreenEdge.Top => bounds.Top + 1,
            ScreenEdge.Bottom => bounds.Bottom - 2,
            _ => 0
        };
    }

    bool IsAtEdge(POINT pt)
    {
        int boundary = EdgeBoundary();
        return _settings.Edge switch
        {
            ScreenEdge.Right => pt.x >= boundary,
            ScreenEdge.Left => pt.x <= boundary,
            ScreenEdge.Top => pt.y <= boundary,
            ScreenEdge.Bottom => pt.y >= boundary,
            _ => false
        };
    }

    void ReturnToWindows()
    {
        Logger.Log("[ReturnToWindows() called]");
        _redirected = false;

        // Any key still physically held was swallowed on the way down; swallow
        // its key-up too so Windows doesn't see an orphaned release.
        foreach (int vk in _heldVks) _suppressUpVks.Add(vk);
        _heldVks.Clear();
        _heldUsages.Clear();

        _modifiers = 0;
        _currentButtons = 0;
        _virtualOutward = 0;
        _returnArmed = false;
        _mouseRemainderX = _mouseRemainderY = 0;
        _osWheelAccumulator = _osHWheelAccumulator = 0;
        EndScrollGesture();

        _keyboardPump.Post(0, Array.Empty<byte>());
        _mousePump.Post(0, 0, 0, 0, 0);

        // Nudge the real cursor a little inside the boundary so we don't
        // instantly re-trigger the same edge crossing.
        var bounds = SystemInformation.VirtualScreen;
        int boundary = EdgeBoundary();
        var current = Cursor.Position;
        var target = _settings.Edge switch
        {
            ScreenEdge.Right => new Point(boundary - 10, current.Y),
            ScreenEdge.Left => new Point(boundary + 10, current.Y),
            ScreenEdge.Top => new Point(current.X, boundary + 10),
            ScreenEdge.Bottom => new Point(current.X, boundary - 10),
            _ => current
        };
        Cursor.Position = new Point(
            Math.Clamp(target.X, bounds.Left, bounds.Right - 1),
            Math.Clamp(target.Y, bounds.Top, bounds.Bottom - 1));

        UpdateStatusTextAsync();
        Logger.Log("[back to Windows]");
    }

    // --- hooks ---

    IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);

        int msg = wParam.ToInt32();

        if (_calibrating)
        {
            if (msg == WM_LBUTTONDOWN)
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                HandleCalibrationClick(hookStruct.pt);
                return (IntPtr)1;
            }

            // Movement passes through - suppressing it would freeze the OS's
            // tracked cursor position, so every corner would record wherever
            // the cursor was when calibration started. Everything else is
            // swallowed, including the button-ups matching the clicks above,
            // which previously leaked to whatever was underneath.
            if (msg != WM_MOUSEMOVE) return (IntPtr)1;
            return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
        }

        if (!_redirected)
        {
            if (msg == WM_MOUSEMOVE)
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (IsAtEdge(hookStruct.pt))
                {
                    _redirected = true;
                    _currentButtons = 0;
                    _virtualOutward = 0;
                    _returnArmed = false;
                    UpdateStatusTextAsync();
                    Logger.Log($"[handed off to iPad - press {(Keys)_settings.ReturnHotkeyVk} to return]");
                    return (IntPtr)1;
                }
            }
            return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
        }

        // Redirected: the wheel is handled here because Windows delivers
        // touchpad two-finger scroll as a classic WM_MOUSEWHEEL message rather
        // than through Raw Input. Movement and clicks come from Raw Input in
        // HandleRawInput; everything else from the legacy pipeline is dropped.
        if (msg == WM_MOUSEWHEEL || msg == WM_MOUSEHWHEEL)
        {
            var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            short rawDelta = (short)(hookStruct.mouseData >> 16);
            long now = Environment.TickCount64;
            bool horizontal = msg == WM_MOUSEHWHEEL;

            // Tracked per axis: a touchpad whose driver synthesises one but not
            // the other shouldn't have the raw-HID fallback disabled for both.
            if (horizontal) _lastOsHWheelTicks = now;
            else _lastOsWheelTicks = now;

            // High-resolution wheels and precision touchpads send deltas
            // smaller than one notch. Accumulating them is what makes smooth
            // scrolling work; rounding each one up to a full notch - as this
            // used to - scrolls far too fast.
            double scaled = rawDelta * _settings.ScrollSpeed;
            int notches;
            if (horizontal)
            {
                _osHWheelAccumulator += scaled;
                notches = (int)(_osHWheelAccumulator / WHEEL_DELTA);
                _osHWheelAccumulator -= notches * (double)WHEEL_DELTA;
            }
            else
            {
                _osWheelAccumulator += scaled;
                notches = (int)(_osWheelAccumulator / WHEEL_DELTA);
                _osWheelAccumulator -= notches * (double)WHEEL_DELTA;
            }

            if (notches != 0)
                PostWheel(horizontal ? 0 : notches, horizontal ? notches : 0,
                    horizontal ? "wm_mousehwheel" : "wm_mousewheel");
            else
                _lastScrollTicks = now; // still a scroll, just short of a notch
        }

        return (IntPtr)1;
    }

    IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);

        int msg = wParam.ToInt32();
        var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        int vk = (int)hookStruct.vkCode;
        bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
        bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

        // Calibration suppresses all input; Esc is the way out. Without this
        // the overlay's promise that input is captured was only half true, and
        // a calibration you didn't want to finish had no exit.
        if (_calibrating)
        {
            if (isDown && vk == VkEscape) CancelCalibration();
            return (IntPtr)1;
        }

        // The configured return hotkey only does anything while redirected -
        // otherwise it behaves as a normal key for local use.
        if (vk == _settings.ReturnHotkeyVk && isDown && _redirected)
        {
            _suppressUpVks.Add(vk);
            ReturnToWindows();
            return (IntPtr)1;
        }

        if (_redirected)
        {
            if (isDown || isUp)
            {
                UpdateModifier(vk, isDown);

                if (isDown) _heldVks.Add(vk);
                else _heldVks.Remove(vk);

                if (VkToHidUsage.TryGetValue(vk, out byte usage))
                {
                    if (isDown && !_heldUsages.Contains(usage) && _heldUsages.Count < 6)
                        _heldUsages.Add(usage);
                    else if (isUp)
                        _heldUsages.Remove(usage);
                }

                _keyboardPump.Post(_modifiers, _heldUsages);
            }
            return (IntPtr)1; // suppressed locally while redirected
        }

        // Not redirected, but this key went down while we were - drop the
        // matching up so applications don't see a release with no press.
        if (isUp && _suppressUpVks.Remove(vk)) return (IntPtr)1;

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

    /// <summary>
    /// Virtual-key to HID usage map. Usages must stay within 0..101, the range
    /// declared by the keyboard collection in the report descriptor - which is
    /// why F13-F24 are absent.
    /// </summary>
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

        map[0x2C] = 0x46; // Print Screen
        map[0x91] = 0x47; // Scroll Lock
        map[0x13] = 0x48; // Pause
        map[0x2D] = 0x49; // Insert
        map[0x24] = 0x4A; // Home
        map[0x21] = 0x4B; // Page Up
        map[0x2E] = 0x4C; // Delete
        map[0x23] = 0x4D; // End
        map[0x22] = 0x4E; // Page Down
        map[0x27] = 0x4F; // Right
        map[0x25] = 0x50; // Left
        map[0x28] = 0x51; // Down
        map[0x26] = 0x52; // Up

        // Numeric keypad - previously missing entirely, so numpad input was
        // silently dropped while redirected.
        map[0x90] = 0x53; // Num Lock
        map[0x6F] = 0x54; // Divide
        map[0x6A] = 0x55; // Multiply
        map[0x6D] = 0x56; // Subtract
        map[0x6B] = 0x57; // Add
        for (int n = 1; n <= 9; n++)
            map[0x60 + n] = (byte)(0x59 + n - 1); // Numpad1..Numpad9
        map[0x60] = 0x62; // Numpad0
        map[0x6E] = 0x63; // Decimal

        map[0x5D] = 0x65; // Application (context menu)

        return map;
    }
}
