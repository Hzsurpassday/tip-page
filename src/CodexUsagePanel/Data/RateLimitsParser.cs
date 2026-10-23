using System.Text.Json;

namespace CodexUsagePanel.Data;

/// <summary>
/// 从 Codex session JSONL 的单行中抽取 <c>payload.rate_limits</c>。
/// 行里同时含用户对话正文，因此这里只读该对象，绝不记录或抛出任何行内容。
/// </summary>
public static class RateLimitsParser
{
    private const string Marker = "\"rate_limits\":";

    public static UsageSnapshot? ParseLines(
        IEnumerable<string> lines, string sourceFile, DateTime writtenAt)
    {
        var buffer = lines as IList<string> ?? lines.ToList();

        // 从后往前：最后写入且带窗口数据的那条就是当前生效额度
        for (int i = buffer.Count - 1; i >= 0; i--)
        {
            var line = buffer[i];
            if (string.IsNullOrEmpty(line)) continue;
            if (line.IndexOf(Marker, StringComparison.Ordinal) < 0) continue;

            var snapshot = TryParseLine(line, sourceFile, writtenAt);
            if (snapshot is not null) return snapshot;
        }

        return null;
    }

    private static UsageSnapshot? TryParseLine(string line, string sourceFile, DateTime writtenAt)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("payload", out var payload)) return null;
            if (payload.ValueKind != JsonValueKind.Object) return null;
            if (!payload.TryGetProperty("rate_limits", out var limits)) return null;
            if (limits.ValueKind != JsonValueKind.Object) return null;

            var primary = ReadWindow(limits, "primary");
            var secondary = ReadWindow(limits, "secondary");

            // premium 之类的记录 primary/secondary 全为 null，不是我们想要的额度窗口
            if (primary is null && secondary is null) return null;

            return new UsageSnapshot(
                primary,
                secondary,
                ReadCredits(limits),
                sourceFile,
                writtenAt);
        }
    }

    private static UsageWindow? ReadWindow(JsonElement limits, string name)
    {
        if (!limits.TryGetProperty(name, out var window)) return null;
        if (window.ValueKind != JsonValueKind.Object) return null;

        if (!window.TryGetProperty("used_percent", out var used)) return null;
        if (used.ValueKind != JsonValueKind.Number) return null;
        if (!used.TryGetDouble(out var usedPercent)) return null;

        int minutes = window.TryGetProperty("window_minutes", out var wm)
                      && wm.ValueKind == JsonValueKind.Number
                      && wm.TryGetInt32(out var parsedMinutes)
            ? parsedMinutes
            : 0;

        long resetsAt = window.TryGetProperty("resets_at", out var ra)
                        && ra.ValueKind == JsonValueKind.Number
                        && ra.TryGetInt64(out var parsedResetsAt)
            ? parsedResetsAt
            : 0;

        return new UsageWindow(usedPercent, minutes, resetsAt);
    }

    private static string? ReadCredits(JsonElement limits)
    {
        if (!limits.TryGetProperty("credits", out var credits)) return null;
        if (credits.ValueKind != JsonValueKind.Object) return null;
        if (!credits.TryGetProperty("balance", out var balance)) return null;

        return balance.ValueKind switch
        {
            JsonValueKind.String => balance.GetString(),
            JsonValueKind.Number => balance.GetRawText(),
            _ => null
        };
    }
}
