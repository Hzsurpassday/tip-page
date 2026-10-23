using CodexUsagePanel.Data;
using CodexUsagePanel.Ui;
using Xunit;

namespace CodexUsagePanel.Tests.Ui;

public class PanelViewTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 17, 17, 0, DateTimeKind.Local);

    private static UsageSnapshot Snapshot(double fiveUsed, double weekUsed, DateTime writtenAt) =>
        new(
            new UsageWindow(fiveUsed, 300, new DateTimeOffset(Now.AddHours(1).AddMinutes(9)).ToUnixTimeSeconds()),
            new UsageWindow(weekUsed, 10080, new DateTimeOffset(Now.AddDays(5).AddHours(15).AddMinutes(40)).ToUnixTimeSeconds()),
            "0",
            @"C:\x\rollout.jsonl",
            writtenAt);

    [Fact]
    public void NoSnapshot_YieldsEmptyState()
    {
        var view = PanelView.Build(null, Now, "PLUS");

        Assert.False(view.HasData);
        Assert.Empty(view.Cards);
        Assert.Equal(0, view.CardColumns);
        Assert.Contains("未检测到", view.EmptyHint);
    }

    [Fact]
    public void TwoWindows_ProduceTwoCardsWithExpectedTitles()
    {
        var view = PanelView.Build(Snapshot(49, 32, Now), Now, "PLUS");

        Assert.True(view.HasData);
        Assert.Equal(2, view.Cards.Count);
        Assert.Equal(2, view.CardColumns);
        Assert.Equal("5 小时额度", view.Cards[0].Title);
        Assert.Equal("5H WINDOW", view.Cards[0].Code);
        Assert.Equal("本周额度", view.Cards[1].Title);
        Assert.Equal("7D WINDOW", view.Cards[1].Code);
    }

    [Fact]
    public void BigNumberIsRemainingPercentWhileLabelReportsUsed()
    {
        // 数据里的 used_percent 是"已用"；界面大数字显示"剩余" = 100 - used
        var view = PanelView.Build(Snapshot(fiveUsed: 51, weekUsed: 32, writtenAt: Now), Now, "PLUS");

        Assert.Equal(49, view.Cards[0].RemainingPercent);
        Assert.Equal("已用 51%", view.Cards[0].UsedLabel);
    }

    [Fact]
    public void CountdownFormats()
    {
        var view = PanelView.Build(Snapshot(49, 32, Now), Now, "PLUS");

        Assert.Equal("1小时 9分", view.Cards[0].Countdown);
        Assert.Equal("5天 15小时 40分", view.Cards[1].Countdown);
    }

    [Fact]
    public void BarsAreAlwaysTwenty()
    {
        var view = PanelView.Build(Snapshot(49, 32, Now), Now, "PLUS");

        Assert.Equal(20, view.Cards[0].Bars.Count);
        Assert.Equal(10, view.Cards[0].Bars.Count(b => b.On));
    }

    [Theory]
    [InlineData(79, "Mint")]
    [InlineData(80, "Amber")]
    [InlineData(100, "Coral")]
    public void AccentTierFollowsUsedPercent(double used, string expected)
    {
        var view = PanelView.Build(Snapshot(used, 32, Now), Now, "PLUS");
        Assert.Equal(expected, view.Cards[0].AccentKey);
    }

    [Fact]
    public void WeeklyCardKeepsMistAccentBelowThresholds()
    {
        var view = PanelView.Build(Snapshot(10, 32, Now), Now, "PLUS");
        Assert.Equal("Mint", view.Cards[0].AccentKey);
        Assert.Equal("Mist", view.Cards[1].AccentKey);
    }

    [Fact]
    public void FreshSnapshotShowsSynchronised()
    {
        var view = PanelView.Build(Snapshot(49, 32, Now.AddSeconds(-30)), Now, "PLUS");

        Assert.True(view.IsSynchronised);
        Assert.Equal("已同步", view.SyncLabel);
        Assert.Equal("30 秒前更新", view.UpdatedLabel);
    }

    [Fact]
    public void StaleSnapshotShowsLaggingState()
    {
        var view = PanelView.Build(Snapshot(49, 32, Now.AddMinutes(-3)), Now, "PLUS");

        Assert.False(view.IsSynchronised);
        Assert.Equal("快照滞后", view.SyncLabel);
        Assert.Equal("3 分钟前更新", view.UpdatedLabel);
    }

    [Fact]
    public void ExpiredResetTime_ShowsUpcomingReset()
    {
        var snapshot = Snapshot(100, 32, Now) with
        {
            Primary = new UsageWindow(100, 300, new DateTimeOffset(Now.AddMinutes(-5)).ToUnixTimeSeconds())
        };

        var view = PanelView.Build(snapshot, Now, "PLUS");

        Assert.Equal("即将重置", view.Cards[0].Countdown);
    }

    [Fact]
    public void TodayLabelUsesChinaWeekdayNames()
    {
        var view = PanelView.Build(Snapshot(10, 10, Now), Now, "PLUS");
        Assert.Equal("10.19 周一", view.TodayLabel);
    }

    [Fact]
    public void ClockShowsHoursAndMinutes()
    {
        var view = PanelView.Build(Snapshot(10, 10, Now), Now, "PLUS");
        Assert.Equal("17:17", view.Clock);
    }

    [Fact]
    public void SingleWindow_ProducesOneFullWidthCard()
    {
        var snapshot = new UsageSnapshot(
            new UsageWindow(20, 300, new DateTimeOffset(Now.AddHours(2)).ToUnixTimeSeconds()),
            null, "0", @"C:\x\rollout.jsonl", Now);

        var view = PanelView.Build(snapshot, Now, "PLUS");

        Assert.Single(view.Cards);
        Assert.Equal(1, view.CardColumns);
    }

    [Fact]
    public void TickUpdatesOnlyTheTimeTexts()
    {
        var view = PanelView.Build(Snapshot(49, 32, Now), Now, "PLUS");
        var card = view.Cards[0];

        var changed = new List<string?>();
        view.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        card.PropertyChanged += (_, e) => changed.Add("Card." + e.PropertyName);

        view.Tick(Now.AddMinutes(1));

        Assert.Equal("17:18", view.Clock);
        Assert.Equal("1小时 8分", card.Countdown);
        Assert.Equal("1 分钟前更新", view.UpdatedLabel);
        Assert.Equal(new[] { "Clock", "UpdatedLabel", "Card.Countdown" }, changed);
    }

    [Fact]
    public void TickWithinTheSameMinuteRaisesNoChangeNotifications()
    {
        var view = PanelView.Build(Snapshot(49, 32, Now), Now, "PLUS");
        int raised = 0;
        view.PropertyChanged += (_, _) => raised++;
        view.Cards[0].PropertyChanged += (_, _) => raised++;

        view.Tick(Now.AddMilliseconds(500));

        Assert.Equal(0, raised);
    }

    [Fact]
    public void CountdownRoundsUpSoItDoesNotDropASecondAfterOpening()
    {
        var view = PanelView.Build(Snapshot(49, 32, Now), Now, "PLUS");

        view.Tick(Now.AddSeconds(1));

        Assert.Equal("1小时 9分", view.Cards[0].Countdown);
    }

    [Fact]
    public void TickOnEmptyStateOnlyMovesTheClock()
    {
        var view = PanelView.Build(null, Now, "PLUS");
        view.Tick(Now.AddMinutes(1));

        Assert.Equal("17:18", view.Clock);
        Assert.Equal(string.Empty, view.UpdatedLabel);
        Assert.Empty(view.Cards);
    }
}
