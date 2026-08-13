using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using static BlePeripheralPoc.NativeMethods;

namespace BlePeripheralPoc;

readonly record struct TouchContact(int Id, int X, int Y);

/// <summary>
/// Decodes Windows Precision Touchpad raw HID reports into finger contacts.
/// Approach adapted from emoacht/RawInput.Touchpad.
///
/// Preparsed data and value caps are cached per device. The previous version
/// called GetRawInputDeviceInfo twice and did two native allocations for
/// *every* report - at touchpad report rates (up to ~1 kHz while fingers are
/// down) that was the single most expensive thing in the input path, and a
/// transient failure of either call silently dropped the report.
/// </summary>
sealed class TouchpadParser : IDisposable
{
    sealed class DeviceCache
    {
        public IntPtr Preparsed;
        public HIDP_VALUE_CAPS[] ValueCaps = Array.Empty<HIDP_VALUE_CAPS>();

        /// <summary>Contact-units of vertical travel per wheel notch, derived from the device's own logical range.</summary>
        public double ScrollDivisor = 40;
    }

    /// <summary>A full-height swipe should be worth roughly this many wheel notches.</summary>
    const double NotchesPerFullSwipe = 30;

    /// <summary>Abandon a partially-assembled frame this long after it started.</summary>
    const int FrameTimeoutMs = 50;

    /// <summary>Hard cap on contacts per frame, in case a device reports a nonsense count.</summary>
    const int MaxFrameContacts = 16;

    readonly Dictionary<IntPtr, DeviceCache?> _devices = new();

    IntPtr _scratch = IntPtr.Zero;
    int _scratchSize;

    // Frame assembly state. Many precision touchpads - including single-contact
    // ones - describe only one contact per HID report and spread a multi-finger
    // frame across consecutive reports, with the contact count present only in
    // the first. Parsing each report in isolation therefore never yields more
    // than one contact, which is what stopped two-finger scroll from ever
    // being recognised.
    readonly List<TouchContact> _frameContacts = new();
    int _frameExpected;
    long _frameStartTicks;
    int _lastLoggedFrameCount = -1;

    /// <summary>Vertical travel per wheel notch for the most recently parsed device.</summary>
    public double ScrollDivisor { get; private set; } = 40;

    /// <summary>
    /// Feeds one HID report into the current frame. Returns the assembled
    /// contacts once a frame is complete, or null while more reports are still
    /// expected (and if the device can't be decoded at all - that failure is
    /// logged once per device rather than once per report).
    /// </summary>
    public List<TouchContact>? Parse(IntPtr hDevice, byte[] reportBytes, int offset, int length)
    {
        var cache = GetCache(hDevice);
        if (cache is null) return null;

        ScrollDivisor = cache.ScrollDivisor;

        IntPtr report = EnsureScratch(length);
        Marshal.Copy(reportBytes, offset, report, length);

        uint contactCount = 0;
        int? curId = null, curX = null, curY = null;
        var allContacts = new List<TouchContact>();

        foreach (var vc in cache.ValueCaps)
        {
            if (HidP_GetUsageValue(HIDP_REPORT_TYPE.HidP_Input, vc.UsagePage, vc.LinkCollection, vc.Usage,
                    out uint value, cache.Preparsed, report, (uint)length) != HIDP_STATUS_SUCCESS)
                continue;

            if (vc.LinkCollection == 0)
            {
                if (vc.UsagePage == UsagePageDigitizer && vc.Usage == UsageContactCount)
                    contactCount = value;
                continue;
            }

            if (vc.UsagePage == UsagePageDigitizer && vc.Usage == UsageContactId) curId = (int)value;
            else if (vc.UsagePage == UsagePageGenericDesktop && vc.Usage == UsageX) curX = (int)value;
            else if (vc.UsagePage == UsagePageGenericDesktop && vc.Usage == UsageY) curY = (int)value;

            if (curId.HasValue && curX.HasValue && curY.HasValue)
            {
                allContacts.Add(new TouchContact(curId.Value, curX.Value, curY.Value));
                curId = curX = curY = null;
                // No early break on contactCount here - if contactCount reads
                // as 0 that would stop after the first contact no matter how
                // many fingers are actually down, which is what broke
                // two-finger scroll detection. Collect everything the
                // descriptor offers and decide which are real below.
            }
        }

        return AssembleFrame(contactCount, allContacts);
    }

    /// <summary>
    /// Collects contacts across the reports that make up one touch frame.
    /// A report carrying a non-zero contact count starts a new frame and says
    /// how many contacts to expect; reports with a count of zero continue it.
    /// </summary>
    List<TouchContact>? AssembleFrame(uint contactCount, List<TouchContact> reportContacts)
    {
        long now = Environment.TickCount64;

        // A dropped report would otherwise leave a frame permanently pending,
        // and with it the "fingers lifted" reset that ends a scroll gesture.
        if (_frameExpected > 0 && now - _frameStartTicks > FrameTimeoutMs)
        {
            _frameContacts.Clear();
            _frameExpected = 0;
        }

        if (contactCount > 0)
        {
            _frameContacts.Clear();
            _frameExpected = (int)Math.Min(contactCount, MaxFrameContacts);
            _frameStartTicks = now;
        }
        else if (_frameExpected == 0)
        {
            // Not continuing anything, and nothing is touching: fingers are up.
            LogFrame(0);
            return new List<TouchContact>();
        }

        _frameContacts.AddRange(reportContacts);

        if (_frameContacts.Count < _frameExpected && _frameContacts.Count < MaxFrameContacts)
            return null; // more reports still to come for this frame

        var frame = new List<TouchContact>(_frameContacts);
        _frameContacts.Clear();
        _frameExpected = 0;
        LogFrame(frame.Count);
        return frame;
    }

    /// <summary>
    /// Logs only when the number of fingers changes. Logging every report meant
    /// a file write every couple of milliseconds during a gesture.
    /// </summary>
    void LogFrame(int count)
    {
        if (count == _lastLoggedFrameCount) return;
        _lastLoggedFrameCount = count;
        Logger.Log($"[touchpad: {count} contact(s)]");
    }

    DeviceCache? GetCache(IntPtr hDevice)
    {
        if (_devices.TryGetValue(hDevice, out var cached)) return cached;

        var cache = BuildCache(hDevice);
        _devices[hDevice] = cache; // negative results cached too - don't retry per report
        return cache;
    }

    static DeviceCache? BuildCache(IntPtr hDevice)
    {
        uint preparsedSize = 0;
        if (GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref preparsedSize) != 0 || preparsedSize == 0)
        {
            Logger.Log($"[touchpad parse: failed getting preparsed data size for device {hDevice}]");
            return null;
        }

        IntPtr preparsed = Marshal.AllocHGlobal((int)preparsedSize);
        try
        {
            if (GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, preparsed, ref preparsedSize) != preparsedSize)
            {
                Logger.Log($"[touchpad parse: failed getting preparsed data for device {hDevice}]");
                Marshal.FreeHGlobal(preparsed);
                return null;
            }

            if (HidP_GetCaps(preparsed, out HIDP_CAPS caps) != HIDP_STATUS_SUCCESS)
            {
                Logger.Log($"[touchpad parse: HidP_GetCaps failed for device {hDevice}]");
                Marshal.FreeHGlobal(preparsed);
                return null;
            }

            ushort valueCapsLength = caps.NumberInputValueCaps;
            var valueCaps = new HIDP_VALUE_CAPS[valueCapsLength];
            if (valueCapsLength > 0 &&
                HidP_GetValueCaps(HIDP_REPORT_TYPE.HidP_Input, valueCaps, ref valueCapsLength, preparsed) != HIDP_STATUS_SUCCESS)
            {
                Logger.Log($"[touchpad parse: HidP_GetValueCaps failed (expected {valueCapsLength} value caps)]");
                Marshal.FreeHGlobal(preparsed);
                return null;
            }

            // Contact Count lives in link collection 0 and each finger in its
            // own collection, so ordering by collection groups each finger's
            // id/x/y together. Sorted once here instead of per report.
            var ordered = valueCaps.OrderBy(v => v.LinkCollection).ToArray();

            var cache = new DeviceCache
            {
                Preparsed = preparsed,
                ValueCaps = ordered,
                ScrollDivisor = DeriveScrollDivisor(ordered)
            };
            Logger.Log($"[touchpad: device {hDevice} ready, {ordered.Length} value caps, scroll divisor {cache.ScrollDivisor:F1}]");
            return cache;
        }
        catch
        {
            Marshal.FreeHGlobal(preparsed);
            throw;
        }
    }

    /// <summary>
    /// Scales raw contact movement to wheel notches using the touchpad's own
    /// logical Y range, so scroll speed is consistent across hardware. The
    /// previous fixed divisor of 20 was a guess in units that vary by orders
    /// of magnitude between devices.
    /// </summary>
    static double DeriveScrollDivisor(HIDP_VALUE_CAPS[] valueCaps)
    {
        foreach (var vc in valueCaps)
        {
            if (vc.LinkCollection == 0 || vc.UsagePage != UsagePageGenericDesktop || vc.Usage != UsageY)
                continue;

            int range = vc.LogicalMax - vc.LogicalMin;
            if (range > 0) return Math.Max(1, range / NotchesPerFullSwipe);
        }
        return 40;
    }

    IntPtr EnsureScratch(int size)
    {
        if (size > _scratchSize)
        {
            if (_scratch != IntPtr.Zero) Marshal.FreeHGlobal(_scratch);
            _scratch = Marshal.AllocHGlobal(size);
            _scratchSize = size;
        }
        return _scratch;
    }

    public void Dispose()
    {
        foreach (var cache in _devices.Values)
        {
            if (cache is not null && cache.Preparsed != IntPtr.Zero)
                Marshal.FreeHGlobal(cache.Preparsed);
        }
        _devices.Clear();

        if (_scratch != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_scratch);
            _scratch = IntPtr.Zero;
            _scratchSize = 0;
        }
    }
}
