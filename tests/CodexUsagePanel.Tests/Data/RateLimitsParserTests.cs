using CodexUsagePanel.Data;
using Xunit;

namespace CodexUsagePanel.Tests.Data;

public class RateLimitsParserTests
{
    // 真实记录形状：外层 event_msg，payload.type = token_count
    private const string RealLine = """
{"timestamp":"2026-07-05T14:32:58.019Z","ordinal":42,"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"total_tokens":185547},"model_context_window":258400},"rate_limits":{"limit_id":"codex","limit_name":null,"primary":{"used_percent":27.0,"window_minutes":300,"resets_at":1783275937},"secondary":{"used_percent":5.0,"window_minutes":10080,"resets_at":1783586585},"credits":{"has_credits":false,"unlimited":false,"balance":null},"individual_limit":null,"spend_control_reached":null,"plan_type":null,"rate_limit_reached_type":null}}}
""";

    [Fact]
    public void ParsesBothWindowsAndCredits()
    {
        var snapshot = RateLimitsParser.ParseLines([RealLine], "f.jsonl", DateTime.UnixEpoch);

        Assert.NotNull(snapshot);
        Assert.Equal(27.0, snapshot!.Primary!.UsedPercent);
        Assert.Equal(300, snapshot.Primary.WindowMinutes);
        Assert.Equal(1783275937, snapshot.Primary.ResetsAtUnixSeconds);
        Assert.Equal(10080, snapshot.Secondary!.WindowMinutes);
        Assert.Null(snapshot.CreditsBalance);   // balance 为 null
        Assert.Equal("f.jsonl", snapshot.SourceFile);
    }

    [Fact]
    public void CreditsBalanceAsStringIsPreserved()
    {
        var line = RealLine.Replace("\"balance\":null", "\"balance\":\"0\"");
        var snapshot = RateLimitsParser.ParseLines([line], "f", DateTime.UnixEpoch);
        Assert.Equal("0", snapshot!.CreditsBalance);
    }

    [Fact]
    public void PremiumRecordWithNullWindows_IsSkipped()
    {
        const string premium = """
{"timestamp":"t","type":"event_msg","payload":{"type":"token_count","rate_limits":{"limit_id":"premium","primary":null,"secondary":null,"credits":{"has_credits":false,"balance":"0"},"plan_type":null}}}
""";
        Assert.Null(RateLimitsParser.ParseLines([premium], "f", DateTime.UnixEpoch));
    }

    [Fact]
    public void LastQualifyingLineWins()
    {
        var first = RealLine.Replace("\"used_percent\":27.0", "\"used_percent\":11.0");
        var second = RealLine.Replace("\"used_percent\":27.0", "\"used_percent\":98.0");

        var snapshot = RateLimitsParser.ParseLines([first, second], "f", DateTime.UnixEpoch);

        Assert.Equal(98.0, snapshot!.Primary!.UsedPercent);
    }

    [Fact]
    public void BrokenJsonIsSkippedAndEarlierLineIsUsed()
    {
        const string broken = """{"payload":{"rate_limits":{"primary":{"used_percent":""";
        var snapshot = RateLimitsParser.ParseLines([RealLine, broken], "f", DateTime.UnixEpoch);
        Assert.Equal(27.0, snapshot!.Primary!.UsedPercent);
    }

    [Fact]
    public void LinesWithoutRateLimits_ReturnNull()
    {
        const string other = """{"timestamp":"t","type":"response_item","payload":{"type":"message"}}""";
        Assert.Null(RateLimitsParser.ParseLines([other, ""], "f", DateTime.UnixEpoch));
    }

    [Fact]
    public void EmptyInput_ReturnsNull()
    {
        Assert.Null(RateLimitsParser.ParseLines([], "f", DateTime.UnixEpoch));
    }
}
