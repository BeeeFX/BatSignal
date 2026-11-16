using System.Text.Json;
using Microsoft.Win32;
using BatSignal.Core.Monitoring;

namespace BatSignal.App;

internal enum DisplayMode { Taskbar, Tray }

/// <summary>User settings plus the last known device readings, stored in %APPDATA%\BatSignal.</summary>
internal sealed class Settings
{
    public DisplayMode Mode { get; set; } = DisplayMode.Taskbar;

    /// <summary>Alert when a device drops to this percentage; 0 disables alerts.</summary>
    public int LowBatteryThreshold { get; set; } = 15;

    public HashSet<string> HiddenDevices { get; set; } = [];

    public List<DeviceSnapshot> LastKnownDevices { get; set; } = [];

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BatSignal");

    static string FilePath => Path.Combine(Folder, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch (Exception e)
        {
            Logger.Write($"Could not read settings, using defaults: {e.Message}");
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception e)
        {
            Logger.Write($"Could not save settings: {e.Message}");
        }
    }

    // Start with Windows (per-user Run key) --------------------------------------------------

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "BatSignal";

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }
}

internal static class Logger
{
    static readonly object Lock = new();
    static string FilePath => Path.Combine(Settings.Folder, "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Settings.Folder);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 512 * 1024) info.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never take the app down
        }
    }
}
