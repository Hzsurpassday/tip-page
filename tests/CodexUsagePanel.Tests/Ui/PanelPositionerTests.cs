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

    [Fact]
    public void ClampsToWorkAreaTopEdge()
    {
        var (_, top) = PanelPositioner.Resolve(
            cursorX: 100, cursorY: 10,
            workLeft: 0, workTop: 50, workRight: 1920, workBottom: 1040,
            dpiScale: 1.0, logicalWidth: 720, logicalHeight: 420, offset: 16);

        Assert.Equal(50, top);
    }

    [Fact]
    public void ClampingAndDpiScaleCombine()
    {
        var (left, top) = PanelPositioner.Resolve(
            cursorX: 1900, cursorY: 1030,
            workLeft: 0, workTop: 0, workRight: 1920, workBottom: 1040,
            dpiScale: 1.5, logicalWidth: 720, logicalHeight: 420, offset: 16);

        Assert.Equal(560, left, 3);          // 840 / 1.5
        Assert.Equal(410 / 1.5, top, 3);     // 410 / 1.5 ≈ 273.333
    }

    [Fact]
    public void FixedPositionWithZeroOffsetLandsExactlyOnTheConfiguredPoint()
    {
        var (left, top) = PanelPositioner.Resolve(
            cursorX: 400, cursorY: 300,
            workLeft: 0, workTop: 0, workRight: 1920, workBottom: 1040,
            dpiScale: 1.5, logicalWidth: 720, logicalHeight: 420, offset: 0);

        Assert.Equal(400 / 1.5, left, 3);
        Assert.Equal(300 / 1.5, top, 3);
    }

    [Fact]
    public void FixedPositionStillClampsInsideWorkArea()
    {
        var (left, top) = PanelPositioner.Resolve(
            cursorX: 1900, cursorY: 1030,
            workLeft: 0, workTop: 0, workRight: 1920, workBottom: 1040,
            dpiScale: 1.0, logicalWidth: 720, logicalHeight: 420, offset: 0);

        Assert.Equal(1200, left);
        Assert.Equal(620, top);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.5)]
    public void ToPhysicalBounds_ScalesDipsToTheSamePixelsTheWindowOccupies(double scale)
    {
        var (left, top, right, bottom) = PanelPositioner.ToPhysicalBounds(80, 60, 720, 420, scale);

        Assert.Equal((int)Math.Floor(80 * scale), left);
        Assert.Equal((int)Math.Floor(60 * scale), top);
        Assert.Equal((int)Math.Ceiling(800 * scale), right);
        Assert.Equal((int)Math.Ceiling(480 * scale), bottom);
        Assert.Equal((int)Math.Ceiling(720 * scale), right - left);
    }

    [Fact]
    public void ToPhysicalBounds_FractionalDipsExpandToWholePixels()
    {
        var (left, top, right, bottom) = PanelPositioner.ToPhysicalBounds(80.4, 60.4, 720.2, 420.2, 1.25);

        Assert.Equal(100, left);      // floor(100.5)
        Assert.Equal(75, top);        // floor(75.5)
        Assert.Equal(1001, right);    // ceil(1000.75)
        Assert.Equal(601, bottom);    // ceil(600.75)
    }

    [Fact]
    public void PanelLargerThanWorkArea_StaysAtWorkAreaOrigin()    {
        var (left, top) = PanelPositioner.Resolve(
            cursorX: 100, cursorY: 100,
            workLeft: 0, workTop: 0, workRight: 800, workBottom: 600,
            dpiScale: 1.5, logicalWidth: 720, logicalHeight: 420, offset: 16);

        Assert.Equal(0, left, 3);
        Assert.Equal(0, top, 3);
    }
}
