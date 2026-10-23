using System.Buffers;
using System.IO;
using System.Text;

namespace CodexUsagePanel.Data;

/// <summary>
/// 在 Codex 的 <c>sessions</c> 目录里挑选最近写入的 rollout 文件，只读尾部窗口，
/// 把窗口内的行交给 <see cref="RateLimitsParser"/>。
/// 单次读取上限 4 MB：绝不整读一个上百 MB 的 session 文件。
/// </summary>
public sealed class CodexUsageReader
{
    private const int InitialTailBytes = 256 * 1024;
    private const int ExpandedTailBytes = 4 * 1024 * 1024;
    private const int MaxCandidateFiles = 3;
    private const int RetryDelayMs = 50;

    private readonly string _sessionsRoot;

    public CodexUsageReader(string sessionsRoot, TimeSpan throttle, Func<DateTime>? now = null)
    {
        _sessionsRoot = sessionsRoot;
        Throttle = throttle;
        Now = now ?? (() => DateTime.Now);
    }

    /// <summary>缓存/节流间隔；本类只保存，由上层（Task 4）决定何时调用 <see cref="Read"/>。</summary>
    public TimeSpan Throttle { get; }

    public Func<DateTime> Now { get; }

    /// <summary>累计真正发生的文件读取次数（用于诊断，不参与业务判断）。</summary>
    public int DiskReadCount { get; private set; }

    /// <summary>累计目录枚举次数。</summary>
    public int DirectoryScanCount { get; private set; }

    public UsageSnapshot? Read()
    {
        foreach (var file in ListCandidates())
        {
            var snapshot = TryReadFile(file);
            if (snapshot is not null) return snapshot;
        }

        return null;
    }

    private List<FileInfo> ListCandidates()
    {
        DirectoryScanCount++;

        if (!Directory.Exists(_sessionsRoot)) return [];

        try
        {
            return new DirectoryInfo(_sessionsRoot)
                .EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(MaxCandidateFiles)
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private UsageSnapshot? TryReadFile(FileInfo file)
    {
        DateTime writtenAt;
        try
        {
            writtenAt = file.LastWriteTime;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var budget in new[] { InitialTailBytes, ExpandedTailBytes })
        {
            var lines = ReadTailLines(file.FullName, budget, out var reachedStart);
            var snapshot = RateLimitsParser.ParseLines(lines, file.FullName, writtenAt);
            if (snapshot is not null) return snapshot;

            // 已经读到文件开头就没有再扩大的必要
            if (reachedStart) return null;
        }

        return null;
    }

    /// <summary>
    /// 读文件最后 <paramref name="maxBytes"/> 字节并拆行。
    /// <paramref name="reachedStart"/> 为 true 表示已经包含文件首行，调用方不必再扩大窗口。
    /// </summary>
    private string[] ReadTailLines(string path, int maxBytes, out bool reachedStart)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                // 共享读写：Codex 正在追加写时也能读
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                long length = stream.Length;
                int take = (int)Math.Min(length, maxBytes);
                reachedStart = take == length;

                if (take == 0) return [];

                stream.Seek(length - take, SeekOrigin.Begin);

                // 复用池化缓冲，避免每次刷新把 256 KB（扩容时 4 MB）丢进 LOH
                var buffer = ArrayPool<byte>.Shared.Rent(take);
                try
                {
                    stream.ReadExactly(buffer, 0, take);
                    DiskReadCount++;

                    int start = 0;
                    if (!reachedStart)
                    {
                        // 丢弃可能被截断的首行；必须限定在 take 范围内，池化数组可能更长
                        int newline = Array.IndexOf(buffer, (byte)'\n', 0, take);
                        if (newline < 0) return [];
                        start = newline + 1;
                    }

                    var text = Encoding.UTF8.GetString(buffer, start, take - start);
                    return text.Split('\n');
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (IOException) when (attempt == 0)
            {
                // 文件被独占或正在替换：等一帧再试一次，仍失败就换下一个候选
                Thread.Sleep(RetryDelayMs);
            }
            catch (IOException)
            {
                reachedStart = true;
                return [];
            }
            catch (UnauthorizedAccessException)
            {
                reachedStart = true;
                return [];
            }
        }

        reachedStart = true;
        return [];
    }
}
