using System.Security;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace BatSignal.App;

/// <summary>
/// Windows notifications (toasts) for an unpackaged desktop app: we register an AppUserModelID
/// with a display name and icon under HKCU, then show toasts under that id.
/// </summary>
internal static class Toasts
{
    const string AppId = "BeeeFX.BatSignal";
    static bool _registered;

    public static bool Show(string title, string body)
    {
        try
        {
            Register();
            var xml = new XmlDocument();
            xml.LoadXml(
                "<toast><visual><binding template=\"ToastGeneric\">" +
                $"<text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(body)}</text>" +
                "</binding></visual></toast>");
            ToastNotificationManager.CreateToastNotifier(AppId).Show(new ToastNotification(xml));
            return true;
        }
        catch (Exception e)
        {
            Logger.Write($"Notification failed: {e.Message}");
            return false;
        }
    }

    static void Register()
    {
        if (_registered) return;
        var icon = Path.Combine(Settings.Folder, "icon.png");
        Directory.CreateDirectory(Settings.Folder);
        using (var src = typeof(Toasts).Assembly.GetManifestResourceStream("BatSignal.icon.png"))
        using (var dst = File.Create(icon))
            src?.CopyTo(dst);

        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}");
        key.SetValue("DisplayName", "BatSignal");
        key.SetValue("IconUri", icon);
        _registered = true;
    }

    /// <summary>Removes the registration (on uninstall).</summary>
    public static void Unregister()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\AppUserModelId\{AppId}", throwOnMissingSubKey: false); }
        catch { }
    }
}
