using System.Runtime.InteropServices;

namespace CodexUsagePanel.Input;

public sealed class DoubleCtrlHook : IDisposable
{
    private readonly CtrlTapDetector _detector;
    private readonly Func<bool> _isPanelVisible;
    private readonly Action _onToggle;
    private readonly Action _onEscape;
    private readonly SynchronizationContext? _uiContext;
    private readonly NativeInterop.LowLevelProc _callback;   // 必须持有引用，防止被 GC 回收
    private IntPtr _handle = IntPtr.Zero;
    private bool _stopped;

    public DoubleCtrlHook(int windowMs, Func<bool> isPanelVisible, Action onToggle, Action onEscape)
    {
        _detector = new CtrlTapDetector(windowMs);
        _isPanelVisible = isPanelVisible;
        _onToggle = onToggle;
        _onEscape = onEscape;
        _uiContext = SynchronizationContext.Current;   // 在 UI 线程构造，后续用它 Post
        _callback = Handle;
    }

    public bool Start()
    {
        _stopped = false;   // 允许暂停后恢复；Stop 之前排队的动作在 Stop 时已被丢弃

        if (_handle != IntPtr.Zero) return true;

        _handle = NativeInterop.SetWindowsHookEx(
            NativeInterop.WH_KEYBOARD_LL,
            _callback,
            NativeInterop.GetModuleHandle(null),
            0);

        return _handle != IntPtr.Zero;
    }

    /// <summary>强制卸载后重装：回调超时被系统静默移除时，句柄非零也不代表钩子还活着。</summary>
    public bool ReArm()
    {
        var previous = _handle;
        _handle = IntPtr.Zero;

        // 句柄失效（钩子已被系统摘掉）时 Unhook 必然失败，所以失败不代表旧钩子仍存活；
        // 但若确实仍存活，重装会出现两个钩子、每次按键触发两次——这比热键失灵更难排查
        if (previous != IntPtr.Zero) NativeInterop.UnhookWindowsHookEx(previous);

        return Start();
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
                bool isUp = wParam == NativeInterop.WM_KEYUP || wParam == NativeInterop.WM_SYSKEYUP;

                if (isUp || wParam == NativeInterop.WM_KEYDOWN || wParam == NativeInterop.WM_SYSKEYDOWN)
                {
                    var data = Marshal.PtrToStructure<NativeInterop.KBDLLHOOKSTRUCT>(lParam);

                    if (CtrlTapDetector.IsCtrlKey((int)data.vkCode))
                    {
                        if (_detector.Feed(isDown: !isUp, isRepeat: false, nowMs: Environment.TickCount64))
                        {
                            Post(_onToggle);
                        }
                    }
                    else if (data.vkCode == NativeInterop.VK_ESCAPE && !isUp && _isPanelVisible())
                    {
                        Post(_onEscape);
                    }
                }
            }
        }
        catch
        {
            // 静默吞掉：钩子只能保证不抛，绝不能影响键盘消息链
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
            var (hook, act) = ((DoubleCtrlHook, Action))state!;
            if (hook._stopped) return;
            act();
        }, (this, action));
    }
}
