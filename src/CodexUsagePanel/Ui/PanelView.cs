using System.ComponentModel;
using CodexUsagePanel.Data;

namespace CodexUsagePanel.Ui;

public sealed record BarView(bool On, string AccentKey);

public sealed class CardView : INotifyPropertyChanged
{
    private string _countdown;

    public CardView(
        string title,
        string code,
        string icon,
        int remainingPercent,
        string usedLabel,
        string accentKey,
        IReadOnlyList<BarView> bars,
        string countdown,
        string resetsAtText,
        long resetsAtUnixSeconds)
    {
        Title = title;
        Code = code;
        Icon = icon;
        RemainingPercent = remainingPercent;
        UsedLabel = usedLabel;
        AccentKey = accentKey;
        Bars = bars;
        _countdown = countdown;
        ResetsAtText = resetsAtText;
        ResetsAtUnixSeconds = resetsAtUnixSeconds;
    }

    public string Title { get; }

    public string Code { get; }

    public string Icon { get; }

    public int RemainingPercent { get; }

    public string UsedLabel { get; }

    public string AccentKey { get; }

    public IReadOnlyList<BarView> Bars { get; }

    public string ResetsAtText { get; }

    public long ResetsAtUnixSeconds { get; }

    public string Countdown
    {
        get => _countdown;
        private set
        {
            if (_countdown == value) return;
            _countdown = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Countdown)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // 每秒只改这一段文本；段数、百分比、配色都不动，避免重建可视树
    public void Tick(DateTime now)
    {
        var resetAt = DateTimeOffset.FromUnixTimeSeconds(ResetsAtUnixSeconds).LocalDateTime;
        Countdown = PanelView.FormatCountdown(resetAt - now);
    }
}

public sealed class PanelView : INotifyPropertyChanged
{
    private string _clock;
    private bool _isSynchronised;
    private string _syncLabel;
    private string _syncAccentKey;
    private string _updatedLabel;

    public PanelView(
        bool hasData,
        bool hasPlanBadge,
        string planLabel,
        string todayLabel,
        string clock,
        bool isSynchronised,
        string syncLabel,
        string syncAccentKey,
        string updatedLabel,
        string creditsLabel,
        string emptyHint,
        IReadOnlyList<CardView> cards,
        DateTime writtenAt)
    {
        HasData = hasData;
        HasPlanBadge = hasPlanBadge;
        PlanLabel = planLabel;
        TodayLabel = todayLabel;
        _clock = clock;
        _isSynchronised = isSynchronised;
        _syncLabel = syncLabel;
        _syncAccentKey = syncAccentKey;
        _updatedLabel = updatedLabel;
        CreditsLabel = creditsLabel;
        EmptyHint = emptyHint;
        Cards = cards;
        WrittenAt = writtenAt;
    }

    public bool HasData { get; }

    public bool HasPlanBadge { get; }

    public string PlanLabel { get; }

    public string TodayLabel { get; }

    public string CreditsLabel { get; }

    public string EmptyHint { get; }

    public IReadOnlyList<CardView> Cards { get; }

    public DateTime WrittenAt { get; }

    public int CardColumns => Cards.Count;

    public string Clock { get => _clock; private set => Set(ref _clock, value, nameof(Clock)); }

    public bool IsSynchronised { get => _isSynchronised; private set => Set(ref _isSynchronised, value, nameof(IsSynchronised)); }

    public string SyncLabel { get => _syncLabel; private set => Set(ref _syncLabel, value, nameof(SyncLabel)); }

    public string SyncAccentKey { get => _syncAccentKey; private set => Set(ref _syncAccentKey, value, nameof(SyncAccentKey)); }

    public string UpdatedLabel { get => _updatedLabel; private set => Set(ref _updatedLabel, value, nameof(UpdatedLabel)); }

    public event PropertyChangedEventHandler? PropertyChanged;

    // 面板可见时的 1 秒心跳：只发上面几个属性的变更通知，不碰 DataContext、不读文件
    public void Tick(DateTime now)
    {
        Clock = now.ToString("HH:mm");

        if (!HasData) return;

        var age = now - WrittenAt;
        bool synchronised = age.TotalSeconds <= 120;

        IsSynchronised = synchronised;
        SyncLabel = synchronised ? "已同步" : "快照滞后";
        SyncAccentKey = synchronised ? "Mint" : "Muted";
        UpdatedLabel = FormatAge(age);

        foreach (var card in Cards) card.Tick(now);
    }

    private void Set<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static readonly string[] WeekdayNames = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    public static PanelView Build(UsageSnapshot? snapshot, DateTime now, string planLabel)
    {
        var today = $"{now.Month:00}.{now.Day:00} {WeekdayNames[(int)now.DayOfWeek]}";
        var clock = now.ToString("HH:mm");
        var badge = !string.IsNullOrWhiteSpace(planLabel);

        if (snapshot is null)
        {
            return new PanelView(
                hasData: false,
                hasPlanBadge: badge,
                planLabel: planLabel,
                todayLabel: today,
                clock: clock,
                isSynchronised: false,
                syncLabel: string.Empty,
                syncAccentKey: "Muted",
                updatedLabel: string.Empty,
                creditsLabel: "0",
                emptyHint: "未检测到 Codex 用量数据\n期望路径：%USERPROFILE%\\.codex\\sessions",
                cards: [],
                writtenAt: now);
        }

        var cards = new List<CardView>();
        bool first = true;

        foreach (var window in snapshot.Windows)
        {
            string slotAccent = first ? "Mint" : "Mist";
            cards.Add(BuildCard(window, now, slotAccent));
            first = false;
        }

        var age = now - snapshot.FileWrittenAt;
        bool synchronised = age.TotalSeconds <= 120;

        return new PanelView(
            hasData: true,
            hasPlanBadge: badge,
            planLabel: planLabel,
            todayLabel: today,
            clock: clock,
            isSynchronised: synchronised,
            syncLabel: synchronised ? "已同步" : "快照滞后",
            syncAccentKey: synchronised ? "Mint" : "Muted",
            updatedLabel: FormatAge(age),
            creditsLabel: string.IsNullOrWhiteSpace(snapshot.CreditsBalance) ? "0" : snapshot.CreditsBalance,
            emptyHint: string.Empty,
            cards: cards,
            writtenAt: snapshot.FileWrittenAt);
    }

    private static CardView BuildCard(UsageWindow window, DateTime now, string slotAccent)
    {
        var (title, code) = window switch
        {
            { IsFiveHour: true } => ("5 小时额度", "5H WINDOW"),
            { IsWeekly: true } => ("本周额度", "7D WINDOW"),
            _ => ($"{window.WindowMinutes / 60} 小时额度", $"{window.WindowMinutes / 60}H WINDOW")
        };

        int used = window.UsedPercentRounded;
        string accent = used >= 100 ? "Coral" : used >= 80 ? "Amber" : slotAccent;
        int filled = window.BarsFilled();

        var bars = new List<BarView>(20);
        for (int i = 0; i < 20; i++) bars.Add(new BarView(i < filled, accent));

        var resetAt = DateTimeOffset.FromUnixTimeSeconds(window.ResetsAtUnixSeconds).LocalDateTime;
        var remaining = resetAt - now;

        return new CardView(
            title: title,
            code: code,
            icon: window.IsWeekly ? "\uE787" : "\uE823",
            remainingPercent: 100 - used,
            usedLabel: $"已用 {used}%",
            accentKey: accent,
            bars: bars,
            countdown: FormatCountdown(remaining),
            resetsAtText: $"{resetAt:yyyy/MM/dd} {WeekdayNames[(int)resetAt.DayOfWeek]} {resetAt:HH:mm}",
            resetsAtUnixSeconds: window.ResetsAtUnixSeconds);
    }

    internal static string FormatCountdown(TimeSpan remaining)
    {
        if (remaining.TotalSeconds <= 0) return "即将重置";
        if (remaining.TotalSeconds < 60) return "不足 1 分钟";

        // 向上取整到分钟：否则弹出后 1 秒计数就会跳掉一整分钟
        int totalMinutes = (int)Math.Ceiling(remaining.TotalSeconds / 60.0);
        int days = totalMinutes / (24 * 60);
        int hours = totalMinutes % (24 * 60) / 60;
        int minutes = totalMinutes % 60;

        if (days > 0) return $"{days}天 {hours}小时 {minutes}分";
        if (hours > 0) return $"{hours}小时 {minutes}分";
        return $"{minutes} 分钟";
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalSeconds < 60) return $"{(int)Math.Max(0, age.TotalSeconds)} 秒前更新";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes} 分钟前更新";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours} 小时前更新";
        return $"{(int)age.TotalDays} 天前更新";
    }
}
