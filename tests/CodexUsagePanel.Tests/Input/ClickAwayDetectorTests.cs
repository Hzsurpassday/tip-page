using CodexUsagePanel.Input;
using Xunit;

namespace CodexUsagePanel.Tests.Input;

public class ClickAwayDetectorTests
{
    [Theory]
    [InlineData(500, 300, false)]   // 内部
    [InlineData(100, 100, false)]   // 左上角边界
    [InlineData(820, 520, false)]   // 右下角边界
    [InlineData(99, 300, true)]     // 左侧外
    [InlineData(821, 300, true)]    // 右侧外
    [InlineData(500, 99, true)]     // 上方外
    [InlineData(500, 521, true)]    // 下方外
    public void IsOutside_UsesInclusiveBounds(int x, int y, bool expected)
    {
        Assert.Equal(expected, ClickAwayDetector.IsOutside(x, y, 100, 100, 820, 520));
    }
}
