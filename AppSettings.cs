using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlePeripheralEmu;

enum ScreenEdge
{
    Left,
    Right,
    Top,
    Bottom
}

sealed class SavedPoint
{
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>
/// User configuration, persisted to %APPDATA%\BlePeripheralEmu\settings.json.
/// Previously nothing was persisted, so every launch re-prompted for the
/// edge/hotkey and re-ran the four-corner calibration.
/// </summary>
sealed class AppSettings
{
    public ScreenEdge Edge { get; set; } = ScreenEdge.Right;

    /// <summary>Virtual-key code of the return-to-Windows hotkey. Defaults to F9.</summary>
    public int ReturnHotkeyVk { get; set; } = (int)Keys.F9;

    /// <summary>
    /// Virtual-key code that types the Windows clipboard out on the iPad.
    /// Deliberately not Ctrl+V, which stays free to reach the iPad's own
    /// clipboard. Defaults to F10.
    /// </summary>
    public int PasteHotkeyVk { get; set; } = (int)Keys.F10;

    /// <summary>
    /// Return control automatically when the tracked pointer comes back past
    /// the edge it left from.
    /// </summary>
    public bool AutoReturnEnabled { get; set; } = true;

    /// <summary>
    /// How far, in raw mouse counts, the pointer is assumed to be able to
    /// travel across the iPad before it's at the far side.
    ///
    /// The iPad never reports its pointer position back, so the position is
    /// dead-reckoned from the deltas already sent. This value caps that
    /// estimate: without it, wandering a long way across the iPad would demand
    /// an equally long journey back before control returned.
    /// </summary>
    public int VirtualTravelCounts { get; set; } = 3500;

    public const int MinTravelCounts = 800;
    public const int MaxTravelCounts = 10000;

    [JsonIgnore]
    public int TravelCounts => Math.Clamp(VirtualTravelCounts, MinTravelCounts, MaxTravelCounts);

    /// <summary>
    /// How hard the pointer must be pushed against the edge, in raw mouse
    /// counts, before control crosses over - and equally, how hard it must be
    /// pushed back to return.
    ///
    /// Reaching the edge alone used to hand off immediately, which made screen
    /// edges unusable for what they're normally for: the taskbar sits on one,
    /// and maximised windows put their controls on another.
    /// </summary>
    public int EdgePushCounts { get; set; } = 250;

    public const int MinEdgePush = 50;
    public const int MaxEdgePush = 800;

    [JsonIgnore]
    public int EdgePush => Math.Clamp(EdgePushCounts, MinEdgePush, MaxEdgePush);

    /// <summary>Flips two-finger / wheel scroll direction, both axes.</summary>
    public bool InvertScroll { get; set; }

    /// <summary>Pointer speed on the iPad, as a percentage. 100 = raw 1:1 deltas.</summary>
    public int MouseSensitivityPercent { get; set; } = 100;

    /// <summary>Scroll speed, as a percentage of the device-derived default.</summary>
    public int ScrollSpeedPercent { get; set; } = 100;

    public const int MinSpeedPercent = 25;
    public const int MaxSpeedPercent = 300;

    [JsonIgnore]
    public double MouseSensitivity => Math.Clamp(MouseSensitivityPercent, MinSpeedPercent, MaxSpeedPercent) / 100.0;

    [JsonIgnore]
    public double ScrollSpeed => Math.Clamp(ScrollSpeedPercent, MinSpeedPercent, MaxSpeedPercent) / 100.0;

    public bool Calibrated { get; set; }

    /// <summary>TopLeft, TopRight, BottomRight, BottomLeft - in virtual-desktop coordinates.</summary>
    public List<SavedPoint> Corners { get; set; } = new();

    [JsonIgnore]
    public static string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BlePeripheralEmu",
        "settings.json");

    /// <summary>
    /// Where settings lived when the app was called "iPad Bridge". Read once, so
    /// the rename doesn't silently throw away an existing calibration.
    /// </summary>
    static readonly string LegacySettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "iPad Bridge",
        "settings.json");

    /// <summary>
    /// True when no settings file existed at load time, i.e. this is a fresh
    /// install. The setup dialog is only forced on first run.
    /// </summary>
    [JsonIgnore]
    public bool IsFirstRun { get; private set; }

    public static AppSettings Load()
    {
        var loaded = TryLoadFrom(SettingsPath);

        if (loaded is null && TryLoadFrom(LegacySettingsPath) is { } migrated)
        {
            Logger.Log("[migrated settings from the previous iPad Bridge location]");
            migrated.Save();
            return migrated;
        }

        return loaded ?? new AppSettings { IsFirstRun = true };
    }

    static AppSettings? TryLoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var loaded = JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJsonContext.Default.AppSettings);
            if (loaded is null) return null;

            loaded.Validate();
            Logger.Log($"[settings loaded from {path}]");
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Logger.Log($"[settings load failed from {path}, using defaults: {ex.Message}]");
            return null;
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, SettingsJsonContext.Default.AppSettings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Log($"[settings save failed: {ex.Message}]");
        }
    }

    /// <summary>
    /// Drops calibration data that can't describe a usable screen rectangle -
    /// e.g. four clicks in roughly the same spot, which would otherwise make
    /// the whole desktop count as "at the edge" and hand off immediately.
    /// </summary>
    void Validate()
    {
        if (!Calibrated) return;

        if (Corners.Count != 4)
        {
            Calibrated = false;
            return;
        }

        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        foreach (var c in Corners)
        {
            minX = Math.Min(minX, c.X);
            maxX = Math.Max(maxX, c.X);
            minY = Math.Min(minY, c.Y);
            maxY = Math.Max(maxY, c.Y);
        }

        if (maxX - minX < 200 || maxY - minY < 200)
        {
            Logger.Log("[settings: calibration spans too small an area, discarding]");
            Calibrated = false;
            Corners.Clear();
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
partial class SettingsJsonContext : JsonSerializerContext
{
}
