using CodexUsagePanel.Ui;
using Xunit;

namespace CodexUsagePanel.Tests.Ui;

public class PanelPositionerTests
{
    [Fact]
    public void PlacesPanelOffsetBelowRightOfCursor()
    {
        var (left, top) = PanelPositioner.Resolve(
            cursorX: 100, cursorY: 100,
            workLeft: 0, workTop: 0, workRight: 1920, workBottom: 1040,
            dpiScale: 1.0, logicalWidth: 720, logicalHeight: 420, offset: 16);

        Assert.Equal(116, left);
        Assert.Equal(116, top);
    }

    [Fact]
    public void ClampsInsideWorkAreaAtBottomRightCorner()
    {
        var (left, top) = PanelPositioner.Resolve(
            cursorX: 1900, cursorY: 1030,
            workLeft: 0, workTop: 0, workRight: 1920, workBottom: 1040,
            dpiScale: 1.0, logicalWidth: 720, logicalHeight: 420, offset: 16);

        Assert.Equal(1200, left);
        Assert.Equal(620, top);
    }

    [Fact]
    public void ConvertsPhysicalPixelsToDipsAtOneHundredFiftyPercent()
    {
        var (left, top) = PanelPositioner.Resolve(
            cursorX: 150, cursorY: 300,
            workLeft: 0, workTop: 0, workRight: 2880, workBottom: 1620,
            dpiScale: 1.5, logicalWidth: 720, logicalHeight: 420, offset: 16);

        Assert.Equal(166 / 1.5, left, 3);
        Assert.Equal(316 / 1.5, top, 3);
    }

    [Fact]
    public void HandlesSecondaryMonitorWithNegativeCoordinates()
    {
        var (left, top) = PanelPositioner.Resolve(
            cursorX: -1900, cursorY: 1020,
            workLeft: -1920, workTop: 0, workRight: 0, workBottom: 1040,
            dpiScale: 1.0, logicalWidth: 720, logicalHeight: 420, offset: 16);

        Assert.Equal(-1884, left);   // -1900 + 16，右侧空间足够，不触发夹回
        Assert.Equal(620, top);      // 1036 + 420 越界 → 1040 - 420
    }

    [Fact]
    public void ClampsToWorkAreaLeftEdge()
    {
        var (left, _) = PanelPositioner.Resolve(
            cursorX: -1900, cursorY: 100,
            workLeft: -1200, workTop: 0, workRight: 0, workBottom: 1040,
            dpiScale: 1.0, logicalWidth: 720, logicalHeight: 420, offset: 16);

        Assert.Equal(-1200, left);
    }
}
