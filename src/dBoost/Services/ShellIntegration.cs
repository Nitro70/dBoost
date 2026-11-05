using System.IO;
using DBoost.Core;
using Microsoft.Win32;

namespace DBoost.App.Services;

/// <summary>
/// Optional Explorer integration, all per-user (HKCU and the user's SendTo folder, no admin):
/// a "Boost volume with dBoost" right-click entry on every audio and video type dBoost knows,
/// and a dBoost shortcut in "Send to". On Windows 11 the right-click entry sits under "Show more options".
/// </summary>
public static class ShellIntegration
{
    private const string Verb = "dBoost";
    private const string MenuText = "Boost volume with dBoost";
    private const string ClassesRoot = @"Software\Classes\SystemFileAssociations";

    public static IEnumerable<string> Extensions => MediaExtensions.Audio.Concat(MediaExtensions.Video).Order(StringComparer.Ordinal);

    public static string CommandFor(string exePath) => $"\"{exePath}\" \"%1\"";

    public static bool IsContextMenuInstalled()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{ClassesRoot}\.mp3\shell\{Verb}\command");
        return key is not null;
    }

    public static void InstallContextMenu(string exePath)
    {
        foreach (string ext in Extensions)
        {
            using var verb = Registry.CurrentUser.CreateSubKey($@"{ClassesRoot}\{ext}\shell\{Verb}");
            verb.SetValue("", MenuText);
            verb.SetValue("Icon", $"\"{exePath}\",0");
            verb.SetValue("MultiSelectModel", "Player");
            using var cmd = verb.CreateSubKey("command");
            cmd.SetValue("", CommandFor(exePath));
        }
        Native.NotifyAssociationsChanged();
    }

    public static void RemoveContextMenu()
    {
        foreach (string ext in Extensions)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesRoot}\{ext}\shell\{Verb}", throwOnMissingSubKey: false); }
            catch (UnauthorizedAccessException) { }
        }
        Native.NotifyAssociationsChanged();
    }

    /// <summary>If the menu is installed but points at an old location of dBoost.exe, point it here.</summary>
    public static void RepairIfMoved(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{ClassesRoot}\.mp3\shell\{Verb}\command");
        if (key?.GetValue("") is string current && !string.Equals(current, CommandFor(exePath), StringComparison.OrdinalIgnoreCase))
            InstallContextMenu(exePath);
    }

    public static string SendToFolder => Environment.GetFolderPath(Environment.SpecialFolder.SendTo);

    private static string SendToLink(string folder) => Path.Combine(folder, "dBoost (boost volume).lnk");

    public static bool IsSendToInstalled(string? folder = null) => File.Exists(SendToLink(folder ?? SendToFolder));

    public static void InstallSendTo(string exePath, string? folder = null)
    {
        string link = SendToLink(folder ?? SendToFolder);
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is not available.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = exePath;
            shortcut.IconLocation = exePath + ",0";
            shortcut.Description = MenuText;
            shortcut.Save();
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    public static void RemoveSendTo(string? folder = null)
    {
        string link = SendToLink(folder ?? SendToFolder);
        if (File.Exists(link)) File.Delete(link);
    }
}
