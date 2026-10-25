using System;
using Microsoft.Win32;

namespace NoEnglishMode;

// Autostart via the per-user Run registry key, toggled from the tray menu
// (default off). The stored path always points at the exe's current location.
internal static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NoEnglishMode";

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (key?.GetValue(ValueName) is not string value)
            {
                return false;
            }

            return string.Equals(Unquote(value), AppFolder.ExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
            {
                Logger.Warn("autostart: could not open the Run registry key");
                return;
            }

            if (enabled)
            {
                key.SetValue(ValueName, $"\"{AppFolder.ExePath}\"");
                Logger.Info($"autostart enabled ({AppFolder.ExePath})");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Logger.Info("autostart disabled");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"autostart update failed: {ex.Message}");
        }
    }

    private static string Unquote(string value) => value.Trim().Trim('"');
}
