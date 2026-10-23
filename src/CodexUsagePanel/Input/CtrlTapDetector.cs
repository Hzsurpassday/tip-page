namespace CodexUsagePanel.Input;

public sealed class CtrlTapDetector
{
    private readonly int _windowMs;
    private long? _lastTapAtMs;
    private bool _ctrlHeld;

    public CtrlTapDetector(int windowMs) => _windowMs = windowMs;

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
