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
        if (_handle != IntPtr.Zero) return true;

        _handle = NativeInterop.SetWindowsHookEx(
            NativeInterop.WH_KEYBOARD_LL,
            _callback,
            NativeInterop.GetModuleHandle(null),
            0);

        return _handle != IntPtr.Zero;
    }

    public void Stop()
    {
        if (_handle == IntPtr.Zero) return;
        NativeInterop.UnhookWindowsHookEx(_handle);
        _handle = IntPtr.Zero;
    }

    public void Dispose() => Stop();

    private IntPtr Handle(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            bool isUp = wParam == NativeInterop.WM_KEYUP || wParam == NativeInterop.WM_SYSKEYUP;

            if (isUp || wParam == NativeInterop.WM_KEYDOWN || wParam == NativeInterop.WM_SYSKEYDOWN)
            {
                var data = Marshal.PtrToStructure<NativeInterop.KBDLLHOOKSTRUCT>(lParam);

                if (data.vkCode == NativeInterop.VK_CONTROL)
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

        // 回调预算约 300ms，超时 Windows 会静默卸载钩子。这里只做 Post，不做 IO / UI。
        return NativeInterop.CallNextHookEx(_handle, nCode, wParam, lParam);
    }

    private void Post(Action action)
    {
        if (_uiContext is null)
        {
            action();
            return;
        }

        _uiContext.Post(static state => ((Action)state!)(), action);
    }
}
