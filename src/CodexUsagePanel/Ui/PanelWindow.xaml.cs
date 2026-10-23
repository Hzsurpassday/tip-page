using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CodexUsagePanel.Input;

namespace CodexUsagePanel.Ui;

public partial class PanelWindow : Window
{
    private readonly DispatcherTimer _tick;
    private readonly Action _onRefresh;
    private readonly Action _onOpenSettings;
    private readonly Action<DateTime> _onTimeTick;
    private readonly Action? _onHidden;
    private int _autoHideSeconds;
    private DateTime _shownAt;

    public PanelWindow(Action onRefresh, Action onOpenSettings, Action<DateTime> onTimeTick, Action? onHidden = null)
    {
        InitializeComponent();
        _onRefresh = onRefresh;
        _onOpenSettings = onOpenSettings;
        _onTimeTick = onTimeTick;
        _onHidden = onHidden;

        // 只在面板可见时跑，避免常驻空转（隐藏时由 HidePanel 显式 Stop）
        _tick = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += OnTick;

        // 任何路径关闭窗口都要停表，不能只靠 HidePanel
        Closed += (_, _) => _tick.Stop();
    }

    public bool IsPanelVisible => IsVisible;

    public (int Left, int Top, int Right, int Bottom)? CurrentBoundsPhysical
    {
        get
        {
            if (!IsVisible) return null;

            var dpi = VisualTreeHelper.GetDpi(this);
            double physicalPerDip = dpi.DpiScaleX;

            return (
                (int)Math.Floor(Left * physicalPerDip),
                (int)Math.Floor(Top * physicalPerDip),
                (int)Math.Ceiling((Left + ActualWidth) * physicalPerDip),
                (int)Math.Ceiling((Top + ActualHeight) * physicalPerDip));
        }
    }

    public void ShowPanel(PanelView view, int autoHideSeconds)
    {
        // 已可见时 Show() 不会再走 DPI 校正，先收起以便走完整流程
        if (IsVisible)
        {
            _tick.Stop();
            Hide();
        }

        DataContext = view;
        _autoHideSeconds = autoHideSeconds;

        var cursor = NativeInterop.GetCursorPosition();
        var work = NativeInterop.GetWorkAreaNear(cursor.X, cursor.Y);

        // 先按窗口当前 DPI 摆好：Left/Top 是 DIP，WPF 会用同一 DPI 换算回物理像素，物理位置即正确
        var before = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var (left, top) = PanelPositioner.Resolve(
            cursor.X, cursor.Y, work.Left, work.Top, work.Right, work.Bottom,
            before, Width, Height);
        Left = left;
        Top = top;

        Show();

        // 跨显示器时窗口 DPI 会在 Show 后变化，用真实 DPI 再校正一次（纯计算，不读盘）
        var after = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (Math.Abs(after - before) > 0.001)
        {
            var (correctedLeft, correctedTop) = PanelPositioner.Resolve(
                cursor.X, cursor.Y, work.Left, work.Top, work.Right, work.Bottom,
                after, Width, Height);
            Left = correctedLeft;
            Top = correctedTop;
        }

        _shownAt = DateTime.UtcNow;
        _tick.Start();
    }

    public void Refresh(PanelView view) => DataContext = view;

    public void HidePanel()
    {
        _tick.Stop();
        Hide();

        // 自动隐藏与托盘/热键隐藏共用同一条清理路径：停表、收起后再通知外部释放鼠标钩子
        _onHidden?.Invoke();

        NativeInterop.TrimWorkingSet();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeInterop.GetWindowLongPtr(handle, NativeInterop.GWL_EXSTYLE);
        NativeInterop.SetWindowLongPtr(
            handle,
            NativeInterop.GWL_EXSTYLE,
            (IntPtr)(style.ToInt64() | NativeInterop.WS_EX_TOOLWINDOW | NativeInterop.WS_EX_NOACTIVATE));
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // 只把新的时间交给视图做属性级更新；不重设 DataContext
        try
        {
            _onTimeTick(DateTime.Now);
        }
        catch
        {
            // 心跳失败就收起面板，不让异常冒到 Dispatcher
            HidePanel();
        }

        if ((DateTime.UtcNow - _shownAt).TotalSeconds >= _autoHideSeconds) HidePanel();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _onRefresh();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => _onOpenSettings();
}
