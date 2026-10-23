namespace CodexUsagePanel.Data;

public sealed record UsageWindow(double UsedPercent, int WindowMinutes, long ResetsAtUnixSeconds)
{
    public bool IsFiveHour => WindowMinutes == 300;

    public bool IsWeekly => WindowMinutes == 10080;

    public int UsedPercentRounded =>
        (int)Math.Clamp(Math.Round(UsedPercent, MidpointRounding.AwayFromZero), 0, 100);

    public int BarsFilled(int total = 20) =>
        (int)Math.Round(UsedPercentRounded / 100.0 * total, MidpointRounding.AwayFromZero);
}

public sealed record UsageSnapshot(
    UsageWindow? Primary,
    UsageWindow? Secondary,
    string? CreditsBalance,
    string SourceFile,
    DateTime FileWrittenAt)
{
    public IEnumerable<UsageWindow> Windows
    {
        get
        {
            if (Primary is not null) yield return Primary;
            if (Secondary is not null) yield return Secondary;
        }
    }
}
