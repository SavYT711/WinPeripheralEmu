using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace BlePeripheralEmu;

/// <summary>
/// Sends the Windows clipboard to the iPad by typing it out over the HID
/// keyboard.
///
/// This is not a real clipboard sync and can't be. Over BLE HID we're a
/// peripheral: the only host-to-device channel the profile offers is the
/// keyboard output report, one byte of LED state, so the iPad has no way to
/// hand anything back. Synthesising keystrokes is the whole of what's
/// available, which brings three limits worth knowing about:
///
/// - Plain text only, and only characters the keyboard collection can express.
///   The report descriptor declares usages 0-101, so accented characters,
///   emoji and non-Latin scripts have no representation and are dropped.
/// - Speed is bounded by the BLE connection interval - two reports per
///   character, each waiting its turn behind the others.
/// - Layout-dependent. We send HID usages and the iPad maps them through
///   whichever keyboard layout it is set to, so the mapping below only holds
///   for US English.
/// </summary>
static class ClipboardTyper
{
    /// <summary>Longer clipboards are truncated - at BLE speed the rest would take minutes.</summary>
    public const int MaxChars = 2000;

    /// <summary>
    /// Stop feeding the pump above this depth. Its queue drops the oldest entry
    /// when full, which mid-paste would silently eat characters.
    /// </summary>
    const int PumpHighWater = 8;

    const byte ModifierLeftShift = 0x02;

    /// <summary>
    /// Reads clipboard text on a dedicated STA thread. OLE clipboard access
    /// throws outside an STA apartment, and relying on the process entry point
    /// being STA is fragile with top-level statements.
    /// </summary>
    public static string? ReadText()
    {
        string? text = null;

        var thread = new Thread(() =>
        {
            try
            {
                if (Clipboard.ContainsText()) text = Clipboard.GetText();
            }
            catch (Exception ex)
            {
                // Another process can hold the clipboard open.
                Logger.Log($"[clipboard read failed: {ex.GetType().Name} {ex.Message}]");
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(2));

        return text;
    }

    /// <summary>
    /// Types <paramref name="text"/> through the keyboard pump. Returns the
    /// number of characters that had no HID representation and were skipped.
    /// </summary>
    public static async Task<int> TypeAsync(string text, KeyboardReportPump pump, CancellationToken token)
    {
        var empty = Array.Empty<byte>();
        int skipped = 0;

        foreach (char c in Normalise(text))
        {
            token.ThrowIfCancellationRequested();

            if (!TryMapChar(c, out byte usage, out bool shift))
            {
                skipped++;
                continue;
            }

            pump.Post(shift ? ModifierLeftShift : (byte)0, new[] { usage });
            pump.Post(0, empty); // release, so a repeated character registers twice

            while (pump.PendingCount > PumpHighWater)
                await Task.Delay(5, token).ConfigureAwait(false);
        }

        return skipped;
    }

    /// <summary>
    /// Collapses CRLF to a single newline and truncates to <see cref="MaxChars"/>.
    /// </summary>
    static string Normalise(string text)
    {
        var sb = new StringBuilder(Math.Min(text.Length, MaxChars));
        for (int i = 0; i < text.Length && sb.Length < MaxChars; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') continue; // the \n carries it
                sb.Append('\n');
                continue;
            }
            sb.Append(text[i]);
        }
        return sb.ToString();
    }

    static bool TryMapChar(char c, out byte usage, out bool shift)
    {
        shift = false;

        if (c is >= 'a' and <= 'z') { usage = (byte)(0x04 + (c - 'a')); return true; }
        if (c is >= 'A' and <= 'Z') { usage = (byte)(0x04 + (c - 'A')); shift = true; return true; }
        if (c is >= '1' and <= '9') { usage = (byte)(0x1E + (c - '1')); return true; }
        if (c == '0') { usage = 0x27; return true; }

        if (Unshifted.TryGetValue(c, out usage)) return true;
        if (Shifted.TryGetValue(c, out usage)) { shift = true; return true; }

        usage = 0;
        return false;
    }

    static readonly Dictionary<char, byte> Unshifted = new()
    {
        ['\n'] = 0x28, // Enter
        ['\t'] = 0x2B,
        [' '] = 0x2C,
        ['-'] = 0x2D,
        ['='] = 0x2E,
        ['['] = 0x2F,
        [']'] = 0x30,
        ['\\'] = 0x31,
        [';'] = 0x33,
        ['\''] = 0x34,
        ['`'] = 0x35,
        [','] = 0x36,
        ['.'] = 0x37,
        ['/'] = 0x38
    };

    // Shift plus the usage in the same physical position on a US layout.
    static readonly Dictionary<char, byte> Shifted = new()
    {
        ['!'] = 0x1E,
        ['@'] = 0x1F,
        ['#'] = 0x20,
        ['$'] = 0x21,
        ['%'] = 0x22,
        ['^'] = 0x23,
        ['&'] = 0x24,
        ['*'] = 0x25,
        ['('] = 0x26,
        [')'] = 0x27,
        ['_'] = 0x2D,
        ['+'] = 0x2E,
        ['{'] = 0x2F,
        ['}'] = 0x30,
        ['|'] = 0x31,
        [':'] = 0x33,
        ['"'] = 0x34,
        ['~'] = 0x35,
        ['<'] = 0x36,
        ['>'] = 0x37,
        ['?'] = 0x38
    };
}
