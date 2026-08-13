using System.Collections.Generic;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BlePeripheralPoc;

static class HidHelpers
{
    public static async Task<GattLocalCharacteristic> CreateReadCharacteristic(
        GattLocalService service,
        Guid uuid,
        IBuffer value,
        GattProtectionLevel protectionLevel = GattProtectionLevel.Plain)
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

    public static async Task<GattLocalCharacteristic> CreateInputReportCharacteristic(
        GattLocalService service,
        Guid reportUuid,
        Guid reportReferenceUuid,
        byte reportId)
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
}

/// <summary>
/// Serialises GATT notifications for one characteristic onto a single
/// background pump.
///
/// Reports used to be fired off as <c>_ = NotifyValueAsync(...)</c> from the
/// input hooks. At touchpad report rates that starts dozens of overlapping
/// async operations per second against one characteristic; they complete out
/// of order and the slow ones get dropped by the stack. Scroll notifications
/// were the visible casualty - they're rare compared to movement, so they were
/// exactly the ones lost in the flood.
/// </summary>
abstract class ReportPumpBase : IDisposable
{
    protected readonly GattLocalCharacteristic Characteristic;

    readonly SemaphoreSlim _signal = new(0);
    readonly CancellationTokenSource _cts = new();

    protected readonly object Gate = new();
    bool _signaled;

    // Started on the first Post rather than in the constructor: the pump calls
    // into the derived class, which isn't fully constructed while a base
    // constructor is still running.
    Task? _pumpTask;

    protected ReportPumpBase(GattLocalCharacteristic characteristic)
    {
        Characteristic = characteristic;
    }

    /// <summary>Called under <see cref="Gate"/>; returns false when nothing is queued.</summary>
    protected abstract bool TryDequeue(out byte[] report);

    /// <summary>Called under <see cref="Gate"/>.</summary>
    protected abstract bool IsEmpty { get; }

    /// <summary>Wakes the pump. Callers must not hold <see cref="Gate"/>.</summary>
    protected void Signal()
    {
        bool release;
        lock (Gate)
        {
            _pumpTask ??= Task.Run(PumpAsync);
            release = !_signaled;
            _signaled = true;
        }
        if (release) _signal.Release();
    }

    /// <summary>
    /// Blocks until everything queued has been handed to the Bluetooth stack,
    /// or the timeout expires. Used on shutdown so the "all keys and buttons
    /// released" report isn't cancelled out from under the pump.
    /// </summary>
    public void Flush(int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            lock (Gate)
            {
                if (IsEmpty) break;
            }
            Thread.Sleep(10);
        }
        Thread.Sleep(30); // let the final in-flight notification land
    }

    async Task PumpAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            byte[]? report = null;
            lock (Gate)
            {
                if (TryDequeue(out var next)) report = next;
                else _signaled = false;
            }

            if (report is null)
            {
                try { await _signal.WaitAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            try
            {
                var writer = new DataWriter();
                writer.WriteBytes(report);
                await Characteristic.NotifyValueAsync(writer.DetachBuffer()).AsTask(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // A disconnect mid-notify is normal; don't let it kill the pump.
                Logger.Log($"[notify failed: {ex.GetType().Name} {ex.Message}]");
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _pumpTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _cts.Dispose();
        _signal.Dispose();
    }
}

/// <summary>
/// Mouse reports: [buttons, dx, dy, wheel, pan] - pan being horizontal scroll.
///
/// Movement and scroll deltas accumulate while the previous report is in
/// flight, so nothing is silently discarded. Two consequences worth noting:
/// motion larger than the HID field's +/-127 range is split across several
/// reports instead of being clamped away, and a burst of movement collapses
/// into one report rather than backing up behind the connection interval.
/// Button transitions always start a fresh entry, so a fast click can never
/// be merged out of existence.
/// </summary>
sealed class MouseReportPump : ReportPumpBase
{
    sealed class Entry
    {
        public byte Buttons;
        public int Dx, Dy, Wheel, Pan;
    }

    const int MaxEntries = 32;

    readonly Queue<Entry> _queue = new();

    // Tail of _queue, tracked explicitly: Queue<T> has no indexer, and this
    // is touched on every raw input report.
    Entry? _tail;
    bool _haveLastButtons;
    byte _lastButtons;

    public MouseReportPump(GattLocalCharacteristic characteristic) : base(characteristic) { }

    public void Post(byte buttons, int dx, int dy, int wheel, int pan)
    {
        lock (Gate)
        {
            // Nothing to say: no motion, no scroll, and the host already has
            // this button state.
            if (dx == 0 && dy == 0 && wheel == 0 && pan == 0 && _haveLastButtons && _lastButtons == buttons)
                return;

            if (_tail is null || (_tail.Buttons != buttons && _queue.Count < MaxEntries))
            {
                _tail = new Entry { Buttons = buttons };
                _queue.Enqueue(_tail);
            }
            else if (_tail.Buttons != buttons)
            {
                // Queue is saturated - the link is far behind. Prefer keeping
                // the latest button state over preserving every transition.
                _tail.Buttons = buttons;
            }

            _tail.Dx += dx;
            _tail.Dy += dy;
            _tail.Wheel += wheel;
            _tail.Pan += pan;

            _haveLastButtons = true;
            _lastButtons = buttons;
        }
        Signal();
    }

    protected override bool IsEmpty => _queue.Count == 0;

    protected override bool TryDequeue(out byte[] report)
    {
        report = Array.Empty<byte>();
        if (_queue.Count == 0) return false;

        var head = _queue.Peek();

        sbyte dx = Chunk(ref head.Dx);
        sbyte dy = Chunk(ref head.Dy);
        sbyte wheel = Chunk(ref head.Wheel);
        sbyte pan = Chunk(ref head.Pan);

        report = new[] { head.Buttons, (byte)dx, (byte)dy, (byte)wheel, (byte)pan };

        if (head.Dx == 0 && head.Dy == 0 && head.Wheel == 0 && head.Pan == 0)
        {
            _queue.Dequeue();
            if (_queue.Count == 0) _tail = null;
        }

        return true;
    }

    /// <summary>Takes up to one HID field's worth off <paramref name="remaining"/>.</summary>
    static sbyte Chunk(ref int remaining)
    {
        int take = Math.Clamp(remaining, -127, 127);
        remaining -= take;
        return (sbyte)take;
    }
}

/// <summary>
/// Keyboard reports: [modifiers, reserved, up to 6 key usages].
/// Strict FIFO - each report is a distinct key state, so coalescing would
/// swallow keystrokes.
/// </summary>
sealed class KeyboardReportPump : ReportPumpBase
{
    const int MaxEntries = 128;

    readonly Queue<byte[]> _queue = new();

    public KeyboardReportPump(GattLocalCharacteristic characteristic) : base(characteristic) { }

    public void Post(byte modifiers, IReadOnlyList<byte> usages)
    {
        var report = new byte[8];
        report[0] = modifiers;
        report[1] = 0; // reserved
        for (int i = 0; i < 6 && i < usages.Count; i++)
            report[2 + i] = usages[i];

        lock (Gate)
        {
            // Overflow means the link is dead or hopelessly behind. Drop the
            // oldest; the newest report carries the current key state, and
            // ReturnToWindows always posts an all-zero report last, so this
            // can't leave a key stuck down on the host.
            if (_queue.Count >= MaxEntries) _queue.Dequeue();
            _queue.Enqueue(report);
        }
        Signal();
    }

    protected override bool IsEmpty => _queue.Count == 0;

    protected override bool TryDequeue(out byte[] report)
    {
        if (_queue.Count == 0)
        {
            report = Array.Empty<byte>();
            return false;
        }
        report = _queue.Dequeue();
        return true;
    }
}
