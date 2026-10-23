using System.Runtime.InteropServices;

namespace CodexUsagePanel.Input;

public sealed class ClickAwayHook : IDisposable
{
    private readonly Func<(int Left, int Top, int Right, int Bottom)?> _boundsProvider;
    private readonly Action _onClickAway;
    private readonly SynchronizationContext? _uiContext;
    private readonly NativeInterop.LowLevelProc _callback;   // 必须持有引用，防止被 GC 回收
    private IntPtr _handle = IntPtr.Zero;
    private bool _stopped;

    public ClickAwayHook(Func<(int Left, int Top, int Right, int Bottom)?> boundsProvider, Action onClickAway)
    {
        _boundsProvider = boundsProvider;
        _onClickAway = onClickAway;
        _uiContext = SynchronizationContext.Current;   // 在 UI 线程构造，后续用它 Post
        _callback = Handle;
    }

    public void Start()
    {
        if (_handle != IntPtr.Zero) return;

        _handle = NativeInterop.SetWindowsHookEx(
            NativeInterop.WH_MOUSE_LL,
            _callback,
            NativeInterop.GetModuleHandle(null),
            0);
    }

    public void Stop()
    {
        _stopped = true;   // 先置位：已排队的 Post 与后续回调一律无效

        if (_handle == IntPtr.Zero) return;

        // 卸载失败说明钩子仍存活，保留句柄以便再次卸载；_stopped 已保证不再产生副作用
        if (NativeInterop.UnhookWindowsHookEx(_handle))
        {
            _handle = IntPtr.Zero;
        }
    }

    public void Dispose() => Stop();

    private IntPtr Handle(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // 回调体必须整体兜住异常：任何异常穿越到原生代码都会 fail-fast 崩溃整个进程
        try
        {
            if (nCode >= 0)
            {
                int message = (int)wParam;
                if (message is NativeInterop.WM_LBUTTONDOWN
                            or NativeInterop.WM_RBUTTONDOWN
                            or NativeInterop.WM_MBUTTONDOWN)
                {
                    var bounds = _boundsProvider();
                    if (bounds is { } b)
                    {
                        var data = Marshal.PtrToStructure<NativeInterop.MSLLHOOKSTRUCT>(lParam);
                        if (ClickAwayDetector.IsOutside(data.pt.X, data.pt.Y, b.Left, b.Top, b.Right, b.Bottom))
                        {
                            Post(_onClickAway);
                        }
                    }
                }
            }
        }
        catch
        {
            // 静默吞掉：钩子只能保证不抛，绝不能影响鼠标消息链
        }

        // 回调预算约 300ms，超时 Windows 会静默卸载钩子。这里只做 Post，不做 IO / UI。
        return NativeInterop.CallNextHookEx(_handle, nCode, wParam, lParam);
    }

    private void Post(Action action)
    {
        if (_stopped) return;

        if (_uiContext is null)
        {
            action();
            return;
        }

        // 已排队的回调在 Stop 之后落地时同样丢弃
        _uiContext.Post(static state =>
        {
            var (hook, act) = ((ClickAwayHook, Action))state!;
            if (hook._stopped) return;
            act();
        }, (this, action));
    }
}
