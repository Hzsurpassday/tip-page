using System.IO;
using CodexUsagePanel.Data;
using Xunit;

namespace CodexUsagePanel.Tests.Data;

public sealed class TempSessions : IDisposable
{
    public string Root { get; }

    public TempSessions()
    {
        Root = Path.Combine(Path.GetTempPath(), "cup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string WriteSession(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

public class CodexUsageReaderFileTests
{
    private const string SnapshotLine =
        """{"timestamp":"t","type":"event_msg","payload":{"type":"token_count","rate_limits":{"limit_id":"codex","primary":{"used_percent":49.0,"window_minutes":300,"resets_at":1792352158},"secondary":{"used_percent":32.0,"window_minutes":10080,"resets_at":1792881761},"credits":{"balance":"0"}}}}""";

    // 内容以 " 结尾，原始字符串定界会触发 CS8998，改用普通字符串拼接
    private static string Padding(int chars) =>
        "{\"timestamp\":\"t\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"text\":\"" + new string('x', chars) + "\"}}\n";

    [Fact]
    public void MissingRoot_ReturnsNull()
    {
        var reader = new CodexUsageReader(@"C:\definitely-not-here", TimeSpan.Zero);
        Assert.Null(reader.Read());
    }

    [Fact]
    public void PicksMostRecentlyWrittenFile()
    {
        using var temp = new TempSessions();
        var older = temp.WriteSession(@"2026\10\01\rollout-old.jsonl", SnapshotLine.Replace("49.0", "12.0") + "\n");
        var newer = temp.WriteSession(@"2026\10\02\rollout-new.jsonl", SnapshotLine + "\n");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

        var reader = new CodexUsageReader(temp.Root, TimeSpan.Zero);
        var snapshot = reader.Read();

        Assert.Equal(49.0, snapshot!.Primary!.UsedPercent);
        Assert.EndsWith("rollout-new.jsonl", snapshot.SourceFile);
    }

    [Fact]
    public void ReadsSnapshotFromTailWindow()
    {
        using var temp = new TempSessions();
        var path = temp.WriteSession(@"2026\10\03\rollout-a.jsonl", SnapshotLine + "\n" + Padding(200));

        var reader = new CodexUsageReader(temp.Root, TimeSpan.Zero);
        var snapshot = reader.Read();

        Assert.NotNull(snapshot);
        Assert.True(reader.DiskReadCount > 0);
    }

    [Fact]
    public void RecordFartherBackThanInitialWindow_IsFoundByExpanding()
    {
        using var temp = new TempSessions();
        // 快照后面垫 400 KB 无关行：超出 256 KB 初始窗口，但在 4 MB 扩展窗口内
        var content = SnapshotLine + "\n" + string.Concat(Enumerable.Repeat(Padding(1000), 400));
        temp.WriteSession(@"2026\10\04\rollout-b.jsonl", content);

        var reader = new CodexUsageReader(temp.Root, TimeSpan.Zero);
        Assert.NotNull(reader.Read());
    }

    [Fact]
    public void RecordBeyondExpandedWindow_FallsBackToNextCandidate()
    {
        using var temp = new TempSessions();
        var big = SnapshotLine + "\n" + string.Concat(Enumerable.Repeat(Padding(1000), 6000)); // ~6 MB 尾部
        var bigPath = temp.WriteSession(@"2026\10\05\rollout-big.jsonl", big);
        var smallPath = temp.WriteSession(@"2026\10\06\rollout-small.jsonl", SnapshotLine.Replace("49.0", "77.0") + "\n");
        File.SetLastWriteTimeUtc(smallPath, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(bigPath, DateTime.UtcNow);

        var reader = new CodexUsageReader(temp.Root, TimeSpan.Zero);
        var snapshot = reader.Read();

        Assert.Equal(77.0, snapshot!.Primary!.UsedPercent);
    }

    [Fact]
    public void LockedFile_IsSkippedAfterRetry()
    {
        using var temp = new TempSessions();
        var locked = temp.WriteSession(@"2026\10\07\rollout-locked.jsonl", SnapshotLine.Replace("49.0", "5.0") + "\n");
        var spare = temp.WriteSession(@"2026\10\08\rollout-spare.jsonl", SnapshotLine + "\n");
        File.SetLastWriteTimeUtc(spare, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(locked, DateTime.UtcNow);

        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        var reader = new CodexUsageReader(temp.Root, TimeSpan.Zero);
        var snapshot = reader.Read();

        Assert.Equal(49.0, snapshot!.Primary!.UsedPercent);
    }
}
