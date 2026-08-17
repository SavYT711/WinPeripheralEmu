using System.IO;
using System.Text;

namespace BlePeripheralEmu;

/// <summary>
/// Append-only diagnostic log written next to the .exe. There's no console
/// in this build (WinExe), so this is the only place output goes.
/// </summary>
static class Logger
{
    const long MaxBytes = 2 * 1024 * 1024;

    static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "debug.log");
    static readonly string OldLogPath = Path.Combine(AppContext.BaseDirectory, "debug.log.old");
    static readonly object Lock = new();

    /// <summary>
    /// Rolls the log over if the previous run left it large. Called once at
    /// startup - previously the file grew without bound for the lifetime of
    /// the install.
    /// </summary>
    public static void Initialize()
    {
        lock (Lock)
        {
            try
            {
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Delete(OldLogPath);
                    File.Move(LogPath, OldLogPath);
                }
            }
            catch (IOException) { /* log rotation is best-effort */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static void Log(string message)
    {
        lock (Lock)
        {
            try
            {
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}", Encoding.UTF8);
            }
            catch (IOException) { /* never let logging take the app down */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
