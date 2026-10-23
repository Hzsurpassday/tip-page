using CodexUsagePanel.Data;
using Xunit;

namespace CodexUsagePanel.Tests.Data;

public class CodexUsageReaderCacheTests
{
    private const string Line =
        """{"payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":10.0,"window_minutes":300,"resets_at":1792352158},"secondary":null}}}""";

    [Fact]
    public void SecondReadWithinThrottle_DoesNotTouchDiskOrDirectory()
    {
        using var temp = new TempSessions();
        temp.WriteSession(@"2026\10\09\rollout-a.jsonl", Line + "\n");

        var reader = new CodexUsageReader(temp.Root, TimeSpan.FromSeconds(15));
        reader.Read();
        int reads = reader.DiskReadCount;
        int scans = reader.DirectoryScanCount;

        reader.Read();

        Assert.Equal(reads, reader.DiskReadCount);
        Assert.Equal(scans, reader.DirectoryScanCount);
    }

    [Fact]
    public void AfterThrottleExpires_FileIsReadAgain()
    {
        using var temp = new TempSessions();
        var now = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Local);
        temp.WriteSession(@"2026\10\09\rollout-a.jsonl", Line + "\n");

        var reader = new CodexUsageReader(temp.Root, TimeSpan.FromSeconds(15), () => now);
        reader.Read();
        int reads = reader.DiskReadCount;

        now = now.AddSeconds(16);
        reader.Read();

        Assert.True(reader.DiskReadCount > reads);
    }

    [Fact]
    public void NoDataIsCachedToo()
    {
        using var temp = new TempSessions();
        var now = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Local);
        var reader = new CodexUsageReader(temp.Root, TimeSpan.FromSeconds(15), () => now);

        Assert.Null(reader.Read());
        int scans = reader.DirectoryScanCount;
        Assert.Null(reader.Read());
        Assert.Equal(scans, reader.DirectoryScanCount);
    }

    [Fact]
    public void InvalidateCache_ForcesRescan()
    {
        using var temp = new TempSessions();
        temp.WriteSession(@"2026\10\09\rollout-a.jsonl", Line + "\n");
        var reader = new CodexUsageReader(temp.Root, TimeSpan.FromSeconds(15));

        reader.Read();
        int scans = reader.DirectoryScanCount;
        reader.InvalidateCache();
        reader.Read();

        Assert.Equal(scans + 1, reader.DirectoryScanCount);
    }
}
