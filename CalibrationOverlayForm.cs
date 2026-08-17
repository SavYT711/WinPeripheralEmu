using System.Drawing;
using System.Windows.Forms;

namespace BlePeripheralEmu;

/// <summary>
/// Semi-transparent overlay shown during corner calibration. Purely visual -
/// the corner clicks are captured by the global low-level mouse hook in
/// <see cref="InputBridgeForm"/>, which suppresses them system-wide while
/// calibration is running, so this form never handles clicks itself.
///
/// Covers the whole virtual desktop rather than just the primary monitor, so
/// corners can be marked on any screen.
/// </summary>
sealed class CalibrationOverlayForm : Form
{
    readonly Label _instructionLabel;
    readonly Label _progressLabel;
    readonly Label _hintLabel;

    /// <summary>Overlay origin in virtual-desktop coordinates; corner points arrive in that space.</summary>
    Point _origin;

    public int MarkedCount;
    public POINT[]? Corners;

    public CalibrationOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        _origin = Bounds.Location;
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
            BackColor = Color.Transparent
        };
        Controls.Add(_instructionLabel);

        _hintLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 11, FontStyle.Italic),
            ForeColor = Color.LightGray,
            BackColor = Color.Transparent,
            Text = "Mouse and keyboard are captured until every corner is marked. Press Esc to cancel."
        };
        Controls.Add(_hintLabel);

        Resize += (_, _) => LayoutLabels();
        LayoutLabels();
    }

    /// <summary>
    /// Keeps the instruction centred on the primary monitor rather than the
    /// middle of the whole virtual desktop, which on a multi-monitor setup
    /// can fall in the gap between screens.
    /// </summary>
    void LayoutLabels()
    {
        var primary = Screen.PrimaryScreen?.Bounds ?? Bounds;
        _instructionLabel.Bounds = new Rectangle(
            primary.Left - _origin.X,
            primary.Top - _origin.Y + primary.Height / 3,
            primary.Width,
            140);
        _hintLabel.Location = new Point(40, ClientSize.Height - 60);
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

    public void ShowCancelled()
    {
        _progressLabel.Text = "Cancelled";
        _instructionLabel.Text = "Calibration cancelled";
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
            // Corners are virtual-desktop coordinates; the overlay's origin is
            // not necessarily (0,0) once a second monitor sits above or left
            // of the primary one.
            int x = Corners[i].x - _origin.X;
            int y = Corners[i].y - _origin.Y;
            e.Graphics.FillEllipse(brush, x - 8, y - 8, 16, 16);
            e.Graphics.DrawEllipse(pen, x - 8, y - 8, 16, 16);
        }
    }
}
