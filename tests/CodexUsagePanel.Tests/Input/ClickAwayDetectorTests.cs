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

    // 左侧副屏的虚拟屏坐标是负值：判定必须逐轴做有符号比较，不能把"负数"整体当成"在面板外"
    // 面板占 x ∈ [-1920, -1000]：-1500 在内；-1930 在左边界之外（-1900 其实仍落在区间内）
    [Theory]
    [InlineData(-1500, 300, false)]   // 负坐标区间内部
    [InlineData(-1930, 300, true)]    // 左边界外
    public void IsOutside_HandlesNegativeVirtualScreenBounds(int x, int y, bool expected)
    {
        Assert.Equal(expected, ClickAwayDetector.IsOutside(x, y, -1920, 100, -1000, 520));
    }
}
