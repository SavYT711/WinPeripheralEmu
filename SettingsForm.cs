using System.Windows.Forms;

namespace BlePeripheralEmu;

/// <summary>
/// Setup dialog: which screen edge hands off, the return-to-Windows hotkey,
/// auto-return behaviour and scroll direction. Shown automatically on first
/// run and on demand from the tray menu afterwards.
/// </summary>
sealed class SettingsForm : Form
{
    readonly AppSettings _settings;
    readonly RadioButton _rbLeft, _rbRight, _rbTop, _rbBottom;
    readonly Label _hotkeyLabel;
    readonly Button _setHotkeyButton;
    readonly CheckBox _autoReturnCheck;
    readonly TrackBar _travelBar;
    readonly CheckBox _invertScrollCheck;
    readonly TrackBar _mouseSpeedBar;
    readonly TrackBar _scrollSpeedBar;

    bool _capturingHotkey;
    Keys _hotkey;

    public SettingsForm(AppSettings settings, bool firstRun)
    {
        _settings = settings;
        _hotkey = (Keys)settings.ReturnHotkeyVk;

        Text = firstRun ? "BlePeripheralEmu Setup" : "BlePeripheralEmu Settings";
        ClientSize = new Size(380, 595);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;

        var edgeGroup = new GroupBox { Text = "Which edge hands off to the iPad?", Left = 15, Top = 15, Width = 350, Height = 90 };
        _rbLeft = new RadioButton { Text = "Left", Left = 15, Top = 25, Width = 150, Checked = settings.Edge == ScreenEdge.Left };
        _rbRight = new RadioButton { Text = "Right", Left = 15, Top = 55, Width = 150, Checked = settings.Edge == ScreenEdge.Right };
        _rbTop = new RadioButton { Text = "Top", Left = 180, Top = 25, Width = 150, Checked = settings.Edge == ScreenEdge.Top };
        _rbBottom = new RadioButton { Text = "Bottom", Left = 180, Top = 55, Width = 150, Checked = settings.Edge == ScreenEdge.Bottom };
        edgeGroup.Controls.AddRange(new Control[] { _rbLeft, _rbRight, _rbTop, _rbBottom });

        var hotkeyGroup = new GroupBox { Text = "Return-to-Windows key", Left = 15, Top = 115, Width = 350, Height = 85 };
        _hotkeyLabel = new Label { Text = $"Current: {_hotkey}", Left = 15, Top = 25, Width = 315 };
        _setHotkeyButton = new Button { Text = "Click, then press a key...", Left = 15, Top = 48, Width = 190 };
        _setHotkeyButton.Click += (_, _) =>
        {
            _capturingHotkey = true;
            _hotkeyLabel.Text = "Press any key now (Esc to cancel)...";
            // Disabled while capturing so the button itself can't intercept
            // Enter/Space as a click before the key reaches ProcessCmdKey.
            _setHotkeyButton.Enabled = false;
        };
        hotkeyGroup.Controls.AddRange(new Control[] { _hotkeyLabel, _setHotkeyButton });

        var returnGroup = new GroupBox { Text = "Auto-return", Left = 15, Top = 210, Width = 350, Height = 150 };
        _autoReturnCheck = new CheckBox
        {
            Text = "Return when the pointer comes back to this edge",
            Left = 15,
            Top = 22,
            Width = 320,
            Checked = settings.AutoReturnEnabled
        };

        var travelLabel = new Label { Text = "iPad screen width:", Left = 32, Top = 52, Width = 150 };
        var travelValue = new Label { Left = 235, Top = 52, Width = 100, TextAlign = ContentAlignment.TopRight };
        _travelBar = new TrackBar
        {
            AutoSize = false,
            Left = 30,
            Top = 72,
            Width = 305,
            Height = 38,
            Minimum = AppSettings.MinTravelCounts,
            Maximum = AppSettings.MaxTravelCounts,
            TickFrequency = 1000,
            SmallChange = 100,
            LargeChange = 500,
            Value = Math.Clamp(settings.VirtualTravelCounts, AppSettings.MinTravelCounts, AppSettings.MaxTravelCounts)
        };
        _travelBar.ValueChanged += (_, _) => travelValue.Text = $"{_travelBar.Value}";
        travelValue.Text = $"{_travelBar.Value}";

        var travelHint = new Label
        {
            Text = "Lower this if control comes back too late.",
            Left = 32,
            Top = 115,
            Width = 300,
            ForeColor = SystemColors.GrayText
        };

        _autoReturnCheck.CheckedChanged += (_, _) =>
        {
            _travelBar.Enabled = _autoReturnCheck.Checked;
            travelLabel.Enabled = _autoReturnCheck.Checked;
            travelValue.Enabled = _autoReturnCheck.Checked;
        };
        _travelBar.Enabled = _autoReturnCheck.Checked;
        travelLabel.Enabled = _autoReturnCheck.Checked;
        travelValue.Enabled = _autoReturnCheck.Checked;
        returnGroup.Controls.AddRange(new Control[] { _autoReturnCheck, travelLabel, travelValue, _travelBar, travelHint });

        var speedGroup = new GroupBox { Text = "Speed", Left = 15, Top = 370, Width = 350, Height = 150 };

        var mouseSpeedValue = new Label { Left = 250, Top = 22, Width = 85, TextAlign = ContentAlignment.TopRight };
        _mouseSpeedBar = MakeSpeedBar(settings.MouseSensitivityPercent, 40);
        _mouseSpeedBar.ValueChanged += (_, _) => mouseSpeedValue.Text = FormatSpeed(_mouseSpeedBar.Value);
        mouseSpeedValue.Text = FormatSpeed(_mouseSpeedBar.Value);

        var scrollSpeedValue = new Label { Left = 250, Top = 80, Width = 85, TextAlign = ContentAlignment.TopRight };
        _scrollSpeedBar = MakeSpeedBar(settings.ScrollSpeedPercent, 98);
        _scrollSpeedBar.ValueChanged += (_, _) => scrollSpeedValue.Text = FormatSpeed(_scrollSpeedBar.Value);
        scrollSpeedValue.Text = FormatSpeed(_scrollSpeedBar.Value);

        speedGroup.Controls.AddRange(new Control[]
        {
            new Label { Text = "Pointer speed", Left = 15, Top = 22, Width = 200 },
            mouseSpeedValue,
            _mouseSpeedBar,
            new Label { Text = "Scroll speed", Left = 15, Top = 80, Width = 200 },
            scrollSpeedValue,
            _scrollSpeedBar
        });

        _invertScrollCheck = new CheckBox
        {
            Text = "Reverse scroll direction (both axes)",
            Left = 30,
            Top = 530,
            Width = 300,
            Checked = settings.InvertScroll
        };

        var okButton = new Button
        {
            Text = firstRun ? "Start" : "Save",
            Left = 270,
            Top = 558,
            Width = 95,
            DialogResult = DialogResult.OK
        };
        okButton.Click += (_, _) => Apply();

        var cancelButton = new Button
        {
            Text = "Cancel",
            Left = 165,
            Top = 558,
            Width = 95,
            DialogResult = DialogResult.Cancel
        };

        AcceptButton = okButton;
        CancelButton = cancelButton;

        Controls.AddRange(new Control[]
        {
            edgeGroup, hotkeyGroup, returnGroup, speedGroup, _invertScrollCheck, okButton, cancelButton
        });
    }

    static TrackBar MakeSpeedBar(int value, int top) => new()
    {
        // TrackBar.AutoSize defaults to true and forces its own height,
        // silently ignoring the one set here.
        AutoSize = false,
        Left = 15,
        Top = top,
        Width = 320,
        Height = 38,
        Minimum = AppSettings.MinSpeedPercent,
        Maximum = AppSettings.MaxSpeedPercent,
        TickFrequency = 25,
        SmallChange = 5,
        LargeChange = 25,
        Value = Math.Clamp(value, AppSettings.MinSpeedPercent, AppSettings.MaxSpeedPercent)
    };

    static string FormatSpeed(int percent) => percent == 100 ? "1.00x (default)" : $"{percent / 100.0:0.00}x";

    /// <summary>
    /// Hotkey capture happens here rather than in KeyDown so that dialog keys
    /// - Enter, Tab, Escape, the arrows - can be bound too. In KeyDown they
    /// were consumed by the dialog first: pressing Enter to set a hotkey used
    /// to activate the default button instead.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!_capturingHotkey || (msg.Msg != NativeMethods.WM_KEYDOWN && msg.Msg != NativeMethods.WM_SYSKEYDOWN))
            return base.ProcessCmdKey(ref msg, keyData);

        var key = keyData & Keys.KeyCode;

        if (key == Keys.Escape)
        {
            EndCapture($"Current: {_hotkey}");
            return true;
        }

        if (IsModifierKey(key))
        {
            _hotkeyLabel.Text = "Modifier keys can't be used on their own - try another key.";
            return true;
        }

        _hotkey = key;
        Logger.Log($"[captured hotkey: {_hotkey} = 0x{(int)_hotkey:X2}]");
        EndCapture($"Current: {_hotkey}");
        return true;
    }

    void EndCapture(string label)
    {
        _capturingHotkey = false;
        _hotkeyLabel.Text = label;
        _setHotkeyButton.Enabled = true;
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
        _settings.ReturnHotkeyVk = (int)_hotkey;
        _settings.AutoReturnEnabled = _autoReturnCheck.Checked;
        _settings.VirtualTravelCounts = _travelBar.Value;
        _settings.InvertScroll = _invertScrollCheck.Checked;
        _settings.MouseSensitivityPercent = _mouseSpeedBar.Value;
        _settings.ScrollSpeedPercent = _scrollSpeedBar.Value;
        _settings.Save();

        Logger.Log($"[settings applied - edge: {_settings.Edge}, return key: {_hotkey}, " +
                   $"auto-return: {_settings.AutoReturnEnabled} (travel {_settings.VirtualTravelCounts}), " +
                   $"invert scroll: {_settings.InvertScroll}, " +
                   $"pointer {_settings.MouseSensitivityPercent}%, scroll {_settings.ScrollSpeedPercent}%]");
    }
}
