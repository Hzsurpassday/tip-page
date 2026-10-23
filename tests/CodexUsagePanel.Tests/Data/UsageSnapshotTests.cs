using CodexUsagePanel.Data;
using Xunit;

namespace CodexUsagePanel.Tests.Data;

public class UsageSnapshotTests
{
    [Fact]
    public void FiveHourWindow_IsRecognisedByWindowMinutes()
    {
        var window = new UsageWindow(49.0, 300, 1792352158);
        Assert.True(window.IsFiveHour);
        Assert.False(window.IsWeekly);
    }

    [Fact]
    public void WeeklyWindow_IsRecognisedByWindowMinutes()
    {
        var window = new UsageWindow(32.0, 10080, 1792881761);
        Assert.True(window.IsWeekly);
        Assert.False(window.IsFiveHour);
    }

    [Theory]
    [InlineData(49.4, 49)]
    [InlineData(49.5, 50)]
    [InlineData(-3.0, 0)]
    [InlineData(140.0, 100)]
    public void UsedPercentRounded_ClampsAndRoundsAwayFromZero(double raw, int expected)
    {
        var window = new UsageWindow(raw, 300, 0);
        Assert.Equal(expected, window.UsedPercentRounded);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(49.0, 10)]
    [InlineData(68.0, 14)]
    [InlineData(100.0, 20)]
    public void BarsFilled_MapsPercentToTwentySegments(double raw, int expected)
    {
        var window = new UsageWindow(raw, 300, 0);
        Assert.Equal(expected, window.BarsFilled());
    }

    [Fact]
    public void Windows_EnumeratesOnlyNonNullEntries()
    {
        var onlyPrimary = new UsageSnapshot(new UsageWindow(10, 300, 0), null, null, "a", DateTime.UnixEpoch);
        Assert.Single(onlyPrimary.Windows);

        var both = new UsageSnapshot(new UsageWindow(10, 300, 0), new UsageWindow(20, 10080, 0), "0", "a", DateTime.UnixEpoch);
        Assert.Equal(2, both.Windows.Count());
    }
}
