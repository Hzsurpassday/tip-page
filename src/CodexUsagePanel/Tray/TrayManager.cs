using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using CodexUsagePanel.Data;
using Hardcodet.Wpf.TaskbarNotification;

namespace CodexUsagePanel.Tray;

public sealed class TrayManager : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly TaskbarIcon _icon;
    private readonly MenuItem _autoStartItem;
    private readonly MenuItem _pauseItem;
    private readonly Action _onTogglePause;
    private int _lastIconUsed;

    public TrayManager(Action showPanel, Action exit, Action togglePause, Func<bool> isPaused, Func<bool> autoStartEnabled)
    {
        _onTogglePause = togglePause;

        // 先建图标再建菜单：菜单项的回调会用到 _icon 显示气泡，避免字段可空告警
        _icon = new TaskbarIcon
        {
            ToolTipText = "Codex 额度看板（双击 Ctrl 唤出）",
            Icon = BuildIcon(0)
        };

        var menu = new ContextMenu();

        var show = new MenuItem { Header = "显示面板" };
        show.Click += (_, _) => showPanel();

        _autoStartItem = new MenuItem { Header = "开机自启", IsCheckable = true, IsChecked = autoStartEnabled() };
        _autoStartItem.Click += (_, _) =>
        {
            bool requested = _autoStartItem.IsChecked;
            if (!AutoStart.Set(requested, Environment.ProcessPath ?? string.Empty))
            {
                _autoStartItem.IsChecked = !requested;
                _icon.ShowBalloonTip("开机自启", "写入注册表失败，可能被安全软件拦截。", BalloonIcon.Warning);
            }
        };

        _pauseItem = new MenuItem { Header = "暂停全局热键", IsCheckable = true, IsChecked = isPaused() };

        var quit = new MenuItem { Header = "退出" };
        quit.Click += (_, _) => exit();

        menu.Items.Add(show);
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);

        _icon.ContextMenu = menu;
        _icon.TrayMouseDoubleClick += (_, _) => showPanel();
        _pauseItem.Click += (_, _) => _onTogglePause();
    }

    public void UpdateIcon(UsageSnapshot? snapshot)
    {
        int used = snapshot?.Primary?.UsedPercentRounded ?? 0;

        // 百分比没变就不重画：避免每次唤出都新建 Bitmap + HICON 并触发 Shell 重绘
        if (used == _lastIconUsed) return;
        _lastIconUsed = used;

        var previous = _icon.Icon;
        _icon.Icon = BuildIcon(used);
        previous?.Dispose();
    }

    public void ShowHotkeyWarning() =>
        _icon.ShowBalloonTip("全局热键不可用", "键盘钩子注册失败，请从托盘菜单打开面板。", BalloonIcon.Warning);

    public void Dispose() => _icon.Dispose();

    private static Icon BuildIcon(int usedPercent)
    {
        using var bitmap = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            graphics.Clear(Color.FromArgb(14, 20, 22));

            int filled = (int)Math.Round(Math.Clamp(usedPercent, 0, 100) / 100.0 * 12);
            using var brush = new SolidBrush(usedPercent >= 100
                ? Color.FromArgb(239, 107, 107)
                : usedPercent >= 80
                    ? Color.FromArgb(240, 168, 96)
                    : Color.FromArgb(74, 222, 180));

            if (filled > 0) graphics.FillRectangle(brush, 4, 2 + (12 - filled), 8, filled);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
