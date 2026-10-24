using Microsoft.Win32;

namespace CodexUsagePanel.Tray;

public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexUsagePanel";

    public static string BuildCommand(string exePath) => $"\"{exePath}\" --tray";

    public static bool IsEnabled() => IsEnabled(RunKeyPath, ValueName);

    public static bool Set(bool enabled, string exePath) => Set(enabled, exePath, RunKeyPath, ValueName);

    // 键路径可注入只为测试隔离：读写真实的 Run 键会破坏用户已有的自启设置
    internal static bool IsEnabled(string runKeyPath, string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: false);
            return key?.GetValue(valueName) is string value && value.Length > 0;
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
        catch (System.IO.IOException)
        {
            // 注册表被挂载为不可访问时同样按未启用处理，理由同上
            return false;
        }
    }

    internal static bool Set(bool enabled, string exePath, string runKeyPath, string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(runKeyPath);

            if (key is null) return false;

            if (enabled) key.SetValue(valueName, BuildCommand(exePath));
            else key.DeleteValue(valueName, throwOnMissingValue: false);

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
        catch (System.IO.IOException)
        {
            // 与 IsEnabled 同理：写入失败按"未启用"回报，不让菜单点击把异常抛到 Dispatcher
            return false;
        }
    }
}
