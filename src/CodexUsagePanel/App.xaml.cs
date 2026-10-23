using System.IO;
using System.Windows;
using CodexUsagePanel.Data;
using CodexUsagePanel.Input;
using CodexUsagePanel.Settings;
using CodexUsagePanel.Tray;
using CodexUsagePanel.Ui;

namespace CodexUsagePanel;

public partial class App : Application
{
    private static Mutex? _singleInstance;

    private AppSettings _settings = new();
    private CodexUsageReader _reader = null!;
    private DoubleCtrlHook _keyboardHook = null!;
    private ClickAwayHook _mouseHook = null!;
    private PanelWindow _panel = null!;
    private TrayManager _tray = null!;
    private PanelView? _currentView;
    private bool _hotkeyPaused;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\CodexUsagePanel.SingleInstance", out bool created);
        if (!created)
        {
            Shutdown();
            return;
        }

        _settings = AppSettings.Load(AppSettings.DefaultPath());

        var sessionsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "sessions");

        _reader = new CodexUsageReader(sessionsRoot, TimeSpan.FromSeconds(_settings.RefreshThrottleSeconds));
        // onHidden 覆盖所有隐藏路径（含 10 秒自动隐藏与心跳异常），确保鼠标钩子一定卸载
        _panel = new PanelWindow(
            onRefresh: RefreshNow,
            onOpenSettings: OpenSettingsFolder,
            onTimeTick: OnTimeTick,
            onHidden: () => _mouseHook.Stop());

        // 两个钩子都在 UI 线程构造，内部会用 SynchronizationContext.Current 把动作 Post 回来
        _mouseHook = new ClickAwayHook(() => _panel.CurrentBoundsPhysical, onClickAway: HidePanel);
        _keyboardHook = new DoubleCtrlHook(
            _settings.DoubleCtrlWindowMs,
            isPanelVisible: () => _panel.IsPanelVisible,
            onToggle: TogglePanel,
            onEscape: HidePanel);

        _tray = new TrayManager(
            showPanel: ShowPanel,
            exit: ExitApplication,
            togglePause: TogglePause,
            isPaused: () => _hotkeyPaused,
            autoStartEnabled: AutoStart.IsEnabled);

        if (!_keyboardHook.Start())
        {
            _tray.ShowHotkeyWarning();
        }

        // 启动时读一次，让托盘图标一开始就反映当前用量；之后不再有任何后台定时器
        _tray.UpdateIcon(_reader.Read());
    }

    private void TogglePanel()
    {
        if (_panel.IsPanelVisible) HidePanel();
        else ShowPanel();
    }

    private void ShowPanel()
    {
        var snapshot = _reader.Read();   // 15 秒节流，命中缓存时不碰磁盘
        var view = PanelView.Build(snapshot, DateTime.Now, _settings.PlanLabel);
        _currentView = view;
        _panel.ShowPanel(view, _settings.AutoHideSeconds);
        _mouseHook.Start();
        _tray.UpdateIcon(snapshot);
    }

    private void HidePanel()
    {
        _panel.HidePanel();
        _mouseHook.Stop();
    }

    private void OnTimeTick(DateTime now) => _currentView?.Tick(now);

    private void RefreshNow()
    {
        _reader.InvalidateCache();
        var snapshot = _reader.Read();
        var view = PanelView.Build(snapshot, DateTime.Now, _settings.PlanLabel);
        _currentView = view;

        if (_panel.IsPanelVisible) _panel.Refresh(view);
        _tray.UpdateIcon(snapshot);
    }

    private void TogglePause()
    {
        if (!_hotkeyPaused)
        {
            _keyboardHook.Stop();
            _hotkeyPaused = true;
            _tray.SyncPauseState(true);
            return;
        }

        if (_keyboardHook.Start())
        {
            _hotkeyPaused = false;
            return;
        }

        // 重新注册失败：菜单必须回到"已暂停"，否则用户以为热键生效了
        _tray.SyncPauseState(true);
        _tray.ShowHotkeyWarning();
    }

    private void OpenSettingsFolder()
    {
        var path = AppSettings.DefaultPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) _settings.Save(path);
        using var _ = System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    private void ExitApplication()
    {
        // 只释放单实例锁并请求退出；托盘与钩子的释放统一由 OnExit 负责，避免重复 Dispose
        _singleInstance?.ReleaseMutex();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mouseHook?.Dispose();
        _keyboardHook?.Dispose();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
