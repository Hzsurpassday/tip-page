using System.ComponentModel;
using System.Globalization;
using CodexUsagePanel.Data;

namespace CodexUsagePanel.Ui;

public sealed record BarView(bool On, string AccentKey);

public sealed class CardView : INotifyPropertyChanged
{
    private string _countdown;
    private long _countdownKey = long.MinValue;

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
    // 倒计时按"分钟桶"缓存，桶不变就不做字符串格式化，心跳期间零分配
    public void Tick(DateTime now)
    {
        var resetAt = PanelView.LocalResetTime(ResetsAtUnixSeconds);
        var remaining = resetAt - now;
        long key = CountdownKey(remaining);
        if (key == _countdownKey) return;
        _countdownKey = key;
        Countdown = PanelView.FormatCountdown(remaining);
    }

    private static long CountdownKey(TimeSpan remaining)
    {
        if (remaining.TotalSeconds <= 0) return long.MinValue + 1;
        if (remaining.TotalSeconds < 60) return -1;
        return (long)Math.Ceiling(remaining.TotalSeconds / 60.0);
    }
}

public sealed class PanelView : INotifyPropertyChanged
{
    private string _clock;
    private bool _isSynchronised;
    private string _syncLabel;
    private string _syncAccentKey;
    private string _updatedLabel;
    private long _clockKey = long.MinValue;
    private long _ageKey = long.MinValue;

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
    // 时钟与"几分钟前"都按桶缓存，桶不变时不做任何格式化，避免每秒分配
    public void Tick(DateTime now)
    {
        long clockKey = now.Ticks / TimeSpan.TicksPerMinute;
        if (clockKey != _clockKey)
        {
            _clockKey = clockKey;
            Clock = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        if (!HasData) return;

        var age = now - WrittenAt;
        bool synchronised = age.TotalSeconds <= 120;

        IsSynchronised = synchronised;
        SyncLabel = synchronised ? "已同步" : "快照滞后";
        SyncAccentKey = synchronised ? "Mint" : "Muted";

        long ageKey = AgeKey(age);
        if (ageKey != _ageKey)
        {
            _ageKey = ageKey;
            UpdatedLabel = FormatAge(age);
        }

        for (int i = 0; i < Cards.Count; i++) Cards[i].Tick(now);
    }

    // 把"秒/分钟/小时/天"四个档位编码进一个 long，档位内数值变化才重新格式化
    private static long AgeKey(TimeSpan age)
    {
        if (age.TotalSeconds < 60) return Math.Max(0L, (long)age.TotalSeconds);
        if (age.TotalMinutes < 60) return 1_000L + (long)age.TotalMinutes;
        if (age.TotalHours < 24) return 2_000_000L + (long)age.TotalHours;
        return 3_000_000_000L + (long)age.TotalDays;
    }

    private void Set<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static readonly string[] WeekdayNames = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    /// <remarks>所有 DateTime 必须是本地时间；UTC 会导致倒计时偏移。</remarks>
    public static PanelView Build(UsageSnapshot? snapshot, DateTime now, string planLabel)
    {
        var today = $"{now.Month:00}.{now.Day:00} {WeekdayNames[(int)now.DayOfWeek]}";
        var clock = now.ToString("HH:mm", CultureInfo.InvariantCulture);
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

        foreach (var window in snapshot.Windows)
        {
            cards.Add(BuildCard(window, now, window.IsWeekly ? "Mist" : "Mint"));
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

    private static CardView BuildCard(UsageWindow window, DateTime now, string windowAccent)
    {
        var (title, code) = window switch
        {
            { IsFiveHour: true } => ("5 小时额度", "5H WINDOW"),
            { IsWeekly: true } => ("本周额度", "7D WINDOW"),
            _ => ($"{window.WindowMinutes / 60} 小时额度", $"{window.WindowMinutes / 60}H WINDOW")
        };

        int used = window.UsedPercentRounded;
        string accent = used >= 100 ? "Coral" : used >= 80 ? "Amber" : windowAccent;
        int filled = window.BarsFilled();

        var bars = new List<BarView>(20);
        for (int i = 0; i < 20; i++) bars.Add(new BarView(i < filled, accent));

        var resetAt = LocalResetTime(window.ResetsAtUnixSeconds);
        var remaining = resetAt - now;
        var resetDate = resetAt.ToString("yyyy'/'MM'/'dd", CultureInfo.InvariantCulture);
        var resetClock = resetAt.ToString("HH':'mm", CultureInfo.InvariantCulture);

        return new CardView(
            title: title,
            code: code,
            icon: window.IsWeekly ? "\uE787" : "\uE823",
            remainingPercent: 100 - used,
            usedLabel: $"已用 {used}%",
            accentKey: accent,
            bars: bars,
            countdown: FormatCountdown(remaining),
            resetsAtText: $"{resetDate} {WeekdayNames[(int)resetAt.DayOfWeek]} {resetClock}",
            resetsAtUnixSeconds: window.ResetsAtUnixSeconds);
    }

    // 重置时间是外部数据：越界的 Unix 秒会抛异常，这里统一兜底成 MinValue（显示为"即将重置"）
    private const long MaxUnixSeconds = 253402300799;

    internal static DateTime LocalResetTime(long unixSeconds)
    {
        if (unixSeconds < 0 || unixSeconds > MaxUnixSeconds) return DateTime.MinValue;
        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime;
    }

    internal static string FormatCountdown(TimeSpan remaining)
    {
        if (remaining.TotalSeconds <= 0) return "即将重置";
        if (remaining.TotalSeconds < 60) return "不足 1 分钟";

        // 向上取整到分钟：否则弹出后 1 秒计数就会跳掉一整分钟
        long totalMinutes = (long)Math.Ceiling(remaining.TotalSeconds / 60.0);
        long days = totalMinutes / (24 * 60);
        long hours = totalMinutes % (24 * 60) / 60;
        long minutes = totalMinutes % 60;

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
