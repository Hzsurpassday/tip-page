using Microsoft.Win32;

namespace CodexUsagePanel.Tray;

public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexUsagePanel";

    public static string BuildCommand(string exePath) => $"\"{exePath}\" --tray";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (UnauthorizedAccessException)
        {
            // 读注册表被拒时按"未启用"处理：这个方法在托盘构造期调用，抛异常会直接杀掉启动流程
            return false;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
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
