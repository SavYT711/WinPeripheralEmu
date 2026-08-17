using System.Windows.Forms;

namespace BlePeripheralEmu;

/// <summary>
/// Setup dialog: the handoff edge and how hard to push it, the hotkeys, and
/// pointer/scroll behaviour. Shown automatically on first run and on demand
/// from the tray menu afterwards.
///
/// Tabbed rather than one long column - the single-column version had reached
/// 665px and every new setting made it worse.
/// </summary>
sealed class SettingsForm : Form
{
    enum Capturing { None, Return, Paste }

    readonly AppSettings _settings;
    readonly RadioButton _rbLeft, _rbRight, _rbTop, _rbBottom;
    readonly TrackBar _edgePushBar;
    readonly CheckBox _autoReturnCheck;
    readonly TrackBar _travelBar;
    readonly Label _returnHotkeyLabel, _pasteHotkeyLabel;
    readonly Button _setReturnHotkeyButton, _setPasteHotkeyButton;
    readonly TrackBar _mouseSpeedBar, _scrollSpeedBar;
    readonly CheckBox _invertScrollCheck;

    Capturing _capturing = Capturing.None;
    Keys _returnHotkey, _pasteHotkey;

    public SettingsForm(AppSettings settings, bool firstRun)
    {
        _settings = settings;
        _returnHotkey = (Keys)settings.ReturnHotkeyVk;
        _pasteHotkey = (Keys)settings.PasteHotkeyVk;

        Text = firstRun ? "BlePeripheralEmu Setup" : "BlePeripheralEmu Settings";
        ClientSize = new Size(392, 400);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;

        // --- Handoff tab ---
        var handoff = new TabPage("Handoff");

        _rbLeft = new RadioButton { Text = "Left", Left = 14, Top = 34, Width = 150, Checked = settings.Edge == ScreenEdge.Left };
        _rbRight = new RadioButton { Text = "Right", Left = 14, Top = 59, Width = 150, Checked = settings.Edge == ScreenEdge.Right };
        _rbTop = new RadioButton { Text = "Top", Left = 180, Top = 34, Width = 150, Checked = settings.Edge == ScreenEdge.Top };
        _rbBottom = new RadioButton { Text = "Bottom", Left = 180, Top = 59, Width = 150, Checked = settings.Edge == ScreenEdge.Bottom };

        var pushValue = new Label { Left = 240, Top = 92, Width = 100, TextAlign = ContentAlignment.TopRight };
        _edgePushBar = MakeBar(settings.EdgePushCounts, AppSettings.MinEdgePush, AppSettings.MaxEdgePush, 50, 10, 112);
        _edgePushBar.ValueChanged += (_, _) => pushValue.Text = $"{_edgePushBar.Value}";
        pushValue.Text = $"{_edgePushBar.Value}";

        _autoReturnCheck = new CheckBox
        {
            Text = "Return when the pointer is pushed back to this edge",
            Left = 14,
            Top = 168,
            Width = 330,
            Checked = settings.AutoReturnEnabled
        };

        var travelLabel = new Label { Text = "iPad screen width:", Left = 30, Top = 198, Width = 150 };
        var travelValue = new Label { Left = 240, Top = 198, Width = 100, TextAlign = ContentAlignment.TopRight };
        _travelBar = MakeBar(settings.VirtualTravelCounts, AppSettings.MinTravelCounts, AppSettings.MaxTravelCounts, 1000, 100, 218);
        _travelBar.Left = 30;
        _travelBar.Width = 310;
        _travelBar.ValueChanged += (_, _) => travelValue.Text = $"{_travelBar.Value}";
        travelValue.Text = $"{_travelBar.Value}";

        _autoReturnCheck.CheckedChanged += (_, _) =>
        {
            _travelBar.Enabled = travelLabel.Enabled = travelValue.Enabled = _autoReturnCheck.Checked;
        };
        _travelBar.Enabled = travelLabel.Enabled = travelValue.Enabled = _autoReturnCheck.Checked;

        handoff.Controls.AddRange(new Control[]
        {
            new Label { Text = "Which edge hands off to the iPad?", Left = 14, Top = 12, Width = 330 },
            _rbLeft, _rbRight, _rbTop, _rbBottom,
            new Label { Text = "How hard to push against it:", Left = 14, Top = 92, Width = 220 },
            pushValue, _edgePushBar,
            Hint("Touching the edge does nothing until you push past this.", 14, 140),
            _autoReturnCheck, travelLabel, travelValue, _travelBar,
            Hint("Lower this if control comes back too late.", 30, 260)
        });

        // --- Keys tab ---
        var keys = new TabPage("Keys");

        _returnHotkeyLabel = new Label { Text = $"Current: {_returnHotkey}", Left = 14, Top = 34, Width = 330 };
        _setReturnHotkeyButton = new Button { Text = "Click, then press a key...", Left = 14, Top = 56, Width = 190 };
        _setReturnHotkeyButton.Click += (_, _) => BeginCapture(Capturing.Return);

        _pasteHotkeyLabel = new Label { Text = $"Current: {_pasteHotkey}", Left = 14, Top = 124, Width = 330 };
        _setPasteHotkeyButton = new Button { Text = "Click, then press a key...", Left = 14, Top = 146, Width = 190 };
        _setPasteHotkeyButton.Click += (_, _) => BeginCapture(Capturing.Paste);

        keys.Controls.AddRange(new Control[]
        {
            new Label { Text = "Return to Windows", Left = 14, Top = 12, Width = 330, Font = new Font(Font, FontStyle.Bold) },
            _returnHotkeyLabel, _setReturnHotkeyButton,
            new Label { Text = "Paste clipboard to iPad", Left = 14, Top = 102, Width = 330, Font = new Font(Font, FontStyle.Bold) },
            _pasteHotkeyLabel, _setPasteHotkeyButton,
            Hint("Types the Windows clipboard out on the iPad. Plain ASCII only,\n" +
                 "at typing speed, and it assumes the iPad uses a US layout.\n" +
                 "The iPad's own clipboard is untouched, so Ctrl+V still works\n" +
                 "there. Copying the other way isn't possible over Bluetooth HID.",
                 14, 188, 100)
        });

        // --- Pointer tab ---
        var pointer = new TabPage("Pointer");

        var mouseSpeedValue = new Label { Left = 240, Top = 12, Width = 100, TextAlign = ContentAlignment.TopRight };
        _mouseSpeedBar = MakeBar(settings.MouseSensitivityPercent, AppSettings.MinSpeedPercent, AppSettings.MaxSpeedPercent, 25, 5, 32);
        _mouseSpeedBar.ValueChanged += (_, _) => mouseSpeedValue.Text = FormatSpeed(_mouseSpeedBar.Value);
        mouseSpeedValue.Text = FormatSpeed(_mouseSpeedBar.Value);

        var scrollSpeedValue = new Label { Left = 240, Top = 82, Width = 100, TextAlign = ContentAlignment.TopRight };
        _scrollSpeedBar = MakeBar(settings.ScrollSpeedPercent, AppSettings.MinSpeedPercent, AppSettings.MaxSpeedPercent, 25, 5, 102);
        _scrollSpeedBar.ValueChanged += (_, _) => scrollSpeedValue.Text = FormatSpeed(_scrollSpeedBar.Value);
        scrollSpeedValue.Text = FormatSpeed(_scrollSpeedBar.Value);

        _invertScrollCheck = new CheckBox
        {
            Text = "Reverse scroll direction (both axes)",
            Left = 14,
            Top = 160,
            Width = 330,
            Checked = settings.InvertScroll
        };

        pointer.Controls.AddRange(new Control[]
        {
            new Label { Text = "Pointer speed", Left = 14, Top = 12, Width = 200 },
            mouseSpeedValue, _mouseSpeedBar,
            new Label { Text = "Scroll speed", Left = 14, Top = 82, Width = 200 },
            scrollSpeedValue, _scrollSpeedBar,
            _invertScrollCheck
        });

        var tabs = new TabControl { Left = 12, Top = 12, Width = 368, Height = 330 };
        tabs.TabPages.AddRange(new[] { handoff, keys, pointer });

        var okButton = new Button
        {
            Text = firstRun ? "Start" : "Save",
            Left = 285,
            Top = 355,
            Width = 95,
            DialogResult = DialogResult.OK
        };
        okButton.Click += (_, _) => Apply();

        var cancelButton = new Button
        {
            Text = "Cancel",
            Left = 180,
            Top = 355,
            Width = 95,
            DialogResult = DialogResult.Cancel
        };

        AcceptButton = okButton;
        CancelButton = cancelButton;

        Controls.AddRange(new Control[] { tabs, okButton, cancelButton });
    }

    static Label Hint(string text, int left, int top, int height = 20) => new()
    {
        Text = text,
        Left = left,
        Top = top,
        Width = 330,
        Height = height,
        ForeColor = SystemColors.GrayText
    };

    static TrackBar MakeBar(int value, int min, int max, int tickFrequency, int smallChange, int top) => new()
    {
        // TrackBar.AutoSize defaults to true and forces its own height,
        // silently ignoring the one set here.
        AutoSize = false,
        Left = 14,
        Top = top,
        Width = 326,
        Height = 38,
        Minimum = min,
        Maximum = max,
        TickFrequency = tickFrequency,
        SmallChange = smallChange,
        LargeChange = tickFrequency,
        Value = Math.Clamp(value, min, max)
    };

    static string FormatSpeed(int percent) => percent == 100 ? "1.00x (default)" : $"{percent / 100.0:0.00}x";

    void BeginCapture(Capturing which)
    {
        _capturing = which;
        // Disabled while capturing so the button itself can't intercept
        // Enter/Space as a click before the key reaches ProcessCmdKey.
        _setReturnHotkeyButton.Enabled = false;
        _setPasteHotkeyButton.Enabled = false;

        var label = which == Capturing.Return ? _returnHotkeyLabel : _pasteHotkeyLabel;
        label.Text = "Press any key now (Esc to cancel)...";
    }

    /// <summary>
    /// Hotkey capture happens here rather than in KeyDown so that dialog keys
    /// - Enter, Tab, Escape, the arrows - can be bound too. In KeyDown they
    /// were consumed by the dialog first: pressing Enter to set a hotkey used
    /// to activate the default button instead.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_capturing == Capturing.None ||
            (msg.Msg != NativeMethods.WM_KEYDOWN && msg.Msg != NativeMethods.WM_SYSKEYDOWN))
            return base.ProcessCmdKey(ref msg, keyData);

        var key = keyData & Keys.KeyCode;

        if (key == Keys.Escape)
        {
            EndCapture();
            return true;
        }

        if (IsModifierKey(key))
        {
            var label = _capturing == Capturing.Return ? _returnHotkeyLabel : _pasteHotkeyLabel;
            label.Text = "Modifier keys can't be used on their own - try another key.";
            return true;
        }

        // The two hotkeys must stay distinct, or whichever is checked first in
        // the keyboard hook would shadow the other entirely.
        var other = _capturing == Capturing.Return ? _pasteHotkey : _returnHotkey;
        if (key == other)
        {
            var label = _capturing == Capturing.Return ? _returnHotkeyLabel : _pasteHotkeyLabel;
            label.Text = $"{key} is already the other hotkey - pick a different key.";
            return true;
        }

        if (_capturing == Capturing.Return) _returnHotkey = key;
        else _pasteHotkey = key;

        Logger.Log($"[captured {_capturing} hotkey: {key} = 0x{(int)key:X2}]");
        EndCapture();
        return true;
    }

    void EndCapture()
    {
        _capturing = Capturing.None;
        _returnHotkeyLabel.Text = $"Current: {_returnHotkey}";
        _pasteHotkeyLabel.Text = $"Current: {_pasteHotkey}";
        _setReturnHotkeyButton.Enabled = true;
        _setPasteHotkeyButton.Enabled = true;
    }

    static bool IsModifierKey(Keys key) => key is Keys.ShiftKey or Keys.ControlKey or Keys.Menu
        or Keys.LShiftKey or Keys.RShiftKey
        or Keys.LControlKey or Keys.RControlKey
        or Keys.LMenu or Keys.RMenu
        or Keys.LWin or Keys.RWin;

    void Apply()
    {
        _settings.Edge = _rbLeft.Checked ? ScreenEdge.Left
                       : _rbTop.Checked ? ScreenEdge.Top
                       : _rbBottom.Checked ? ScreenEdge.Bottom
                       : ScreenEdge.Right;
        _settings.EdgePushCounts = _edgePushBar.Value;
        _settings.ReturnHotkeyVk = (int)_returnHotkey;
        _settings.PasteHotkeyVk = (int)_pasteHotkey;
        _settings.AutoReturnEnabled = _autoReturnCheck.Checked;
        _settings.VirtualTravelCounts = _travelBar.Value;
        _settings.MouseSensitivityPercent = _mouseSpeedBar.Value;
        _settings.ScrollSpeedPercent = _scrollSpeedBar.Value;
        _settings.InvertScroll = _invertScrollCheck.Checked;
        _settings.Save();

        Logger.Log($"[settings applied - edge: {_settings.Edge} (push {_settings.EdgePushCounts}), " +
                   $"return: {_returnHotkey}, paste: {_pasteHotkey}, " +
                   $"auto-return: {_settings.AutoReturnEnabled} (travel {_settings.VirtualTravelCounts}), " +
                   $"pointer {_settings.MouseSensitivityPercent}%, scroll {_settings.ScrollSpeedPercent}%, " +
                   $"invert scroll: {_settings.InvertScroll}]");
    }
}
