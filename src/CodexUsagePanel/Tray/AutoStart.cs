using Microsoft.Win32;

namespace CodexUsagePanel.Tray;

public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexUsagePanel";

    public static string BuildCommand(string exePath) => $"\"{exePath}\" --tray";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value && value.Length > 0;
    }

    public static bool Set(bool enabled, string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

            if (key is null) return false;

            if (enabled) key.SetValue(ValueName, BuildCommand(exePath));
            else key.DeleteValue(ValueName, throwOnMissingValue: false);

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }
}
