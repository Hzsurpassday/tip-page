namespace CodexUsagePanel.Input;

public sealed class CtrlTapDetector
{
    private readonly int _windowMs;
    private long? _lastTapAtMs;
    private bool _ctrlHeld;

    public CtrlTapDetector(int windowMs) => _windowMs = windowMs;

    // 低级键盘钩子对物理 Ctrl 上报 VK_LCONTROL / VK_RCONTROL，从不给通用 VK_CONTROL
    public static bool IsCtrlKey(int vkCode) =>
        vkCode is NativeInterop.VK_CONTROL or NativeInterop.VK_LCONTROL or NativeInterop.VK_RCONTROL;

    public bool Feed(bool isDown, bool isRepeat, long nowMs)
    {
        if (!isDown)
        {
            _ctrlHeld = false;
            return false;
        }

        // 长按期间的自动重复不是新的"按下"
        if (_ctrlHeld || isRepeat) return false;

        _ctrlHeld = true;

        if (_lastTapAtMs is long last && nowMs - last <= _windowMs)
        {
            _lastTapAtMs = null;   // 消费掉这次配对，避免连按三下再触发
            return true;
        }

        _lastTapAtMs = nowMs;
        return false;
    }
}
