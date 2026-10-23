using CodexUsagePanel.Input;
using Xunit;

namespace CodexUsagePanel.Tests.Input;

public class CtrlTapDetectorTests
{
    [Fact]
    public void SinglePress_DoesNotTrigger()
    {
        var detector = new CtrlTapDetector(400);
        Assert.False(detector.Feed(isDown: true, isRepeat: false, nowMs: 1000));
        Assert.False(detector.Feed(isDown: false, isRepeat: false, nowMs: 1050));
    }

    [Fact]
    public void TwoQuickPresses_Trigger()
    {
        var detector = new CtrlTapDetector(400);
        detector.Feed(true, false, 1000);
        detector.Feed(false, false, 1040);
        Assert.True(detector.Feed(true, false, 1100));
    }

    [Fact]
    public void TwoPressesBeyondWindow_DoNotTrigger()
    {
        var detector = new CtrlTapDetector(400);
        detector.Feed(true, false, 1000);
        detector.Feed(false, false, 1040);
        Assert.False(detector.Feed(true, false, 1500));
    }

    [Fact]
    public void AutoRepeatWhileHeld_IsIgnored()
    {
        var detector = new CtrlTapDetector(400);
        detector.Feed(true, false, 1000);
        for (int i = 1; i <= 30; i++)
        {
            Assert.False(detector.Feed(true, isRepeat: true, nowMs: 1000 + i * 30));
        }
    }

    [Fact]
    public void ThirdPressAfterTrigger_DoesNotRetriggerImmediately()
    {
        var detector = new CtrlTapDetector(400);
        detector.Feed(true, false, 1000);
        detector.Feed(false, false, 1020);
        Assert.True(detector.Feed(true, false, 1050));
        detector.Feed(false, false, 1070);
        Assert.False(detector.Feed(true, false, 1100));
    }

    [Fact]
    public void SecondDownWithoutRelease_IsIgnored()
    {
        var detector = new CtrlTapDetector(400);
        Assert.False(detector.Feed(true, false, 1000));
        Assert.False(detector.Feed(true, false, 1010));
    }

    [Fact]
    public void ExplicitRepeatFlag_IsIgnored()
    {
        var detector = new CtrlTapDetector(400);
        detector.Feed(true, false, 1000);
        detector.Feed(false, false, 1050);
        Assert.False(detector.Feed(true, isRepeat: true, nowMs: 1100));
    }

    [Fact]
    public void ExactlyAtWindowBoundary_Triggers()
    {
        var detector = new CtrlTapDetector(400);
        detector.Feed(true, false, 1000);
        detector.Feed(false, false, 1040);
        Assert.True(detector.Feed(true, false, 1400));   // 窗口含边界值
    }

    // 低级键盘钩子对物理 Ctrl 上报的是左右专有码，通用 0x11 只在部分注入路径出现
    [Theory]
    [InlineData(0xA2, true)]    // VK_LCONTROL
    [InlineData(0xA3, true)]    // VK_RCONTROL
    [InlineData(0x11, true)]    // VK_CONTROL
    [InlineData(0x1B, false)]   // VK_ESCAPE
    [InlineData(0xA0, false)]   // VK_LSHIFT
    [InlineData(0x41, false)]   // 'A'
    public void IsCtrlKey_AcceptsBothSideSpecificCodes(int vkCode, bool expected)
    {
        Assert.Equal(expected, CtrlTapDetector.IsCtrlKey(vkCode));
    }
}
