using System.Drawing;
using System.Windows.Forms;

namespace BlePeripheralEmu;

/// <summary>
/// Invisible full-desktop window shown while input is redirected to the iPad.
///
/// It exists to swallow input, not to display anything. Suppression is
/// otherwise done by returning 1 from a WH_MOUSE_LL hook, but a low-level
/// mouse hook only sees the legacy message pipeline. Windows routes precision
/// touchpad panning through the modern pointer / DirectManipulation stack for
/// apps that support it - Edge, Chrome, File Explorer, most UWP - and that
/// input is neither visible to the hook nor blockable by it, which is why
/// two-finger scrolling reached the iPad and the laptop at the same time.
///
/// Pointer input is routed to the window under the cursor, so parking an
/// inert window there gives the leaked gestures somewhere harmless to land.
/// </summary>
sealed class SuppressionOverlayForm : Form
{
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WS_EX_TOOLWINDOW = 0x00000080;

    static readonly Cursor BlankCursor = CreateBlankCursor();

    public SuppressionOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.Black;

        // Layered and effectively invisible, but still hit-testable. Note this
        // deliberately does NOT set WS_EX_TRANSPARENT: that would make the
        // window click-through, which is exactly what must not happen here.
        Opacity = 0.01;

        Cursor = BlankCursor;
    }

    /// <summary>Never take focus - the user's app underneath stays active.</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW; // no focus, no Alt-Tab entry
            return cp;
        }
    }

    public void ShowOverlay()
    {
        // Re-read on every show: a monitor may have been added, removed or
        // rearranged since this form was constructed.
        Bounds = SystemInformation.VirtualScreen;

        Show();
        BringToFront();

        // The form's Cursor property handles WM_SETCURSOR from here on, but
        // that only arrives with a mouse message - and mouse messages are
        // being suppressed. Set the cursor directly so it disappears now
        // rather than at the next event that manages to get through.
        Cursor.Current = BlankCursor;
    }

    public void HideOverlay()
    {
        Hide();
        Cursor.Current = Cursors.Default;
    }

    /// <summary>
    /// A fully transparent cursor. Preferred over Cursor.Hide(), which is a
    /// process-wide refcount that leaves the pointer permanently invisible if
    /// a hide is ever left unbalanced by a show.
    /// </summary>
    static Cursor CreateBlankCursor()
    {
        // A new Bitmap is fully transparent, which is all this needs to be.
        using var bitmap = new Bitmap(32, 32);
        return new Cursor(bitmap.GetHicon());
    }
}
