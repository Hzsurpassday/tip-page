using System.Buffers;
using System.IO;
using System.Text;

namespace CodexUsagePanel.Data;

/// <summary>
/// 在 Codex 的 <c>sessions</c> 目录里挑选最近写入的 rollout 文件，只读尾部窗口，
/// 把窗口内的行交给 <see cref="RateLimitsParser"/>。
/// 单次读取上限 4 MB：绝不整读一个上百 MB 的 session 文件。
/// </summary>
/// <remarks>非线程安全：字段无同步，只在 UI 线程调用。</remarks>
public sealed class CodexUsageReader
{
    private const int InitialTailBytes = 256 * 1024;
    private const int ExpandedTailBytes = 4 * 1024 * 1024;
    private const int MaxCandidateFiles = 3;
    private const int RetryDelayMs = 50;

    private readonly string _sessionsRoot;

    private UsageSnapshot? _cached;
    private DateTime _lastScanAt = DateTime.MinValue;
    private bool _hasScanned;

    public CodexUsageReader(string sessionsRoot, TimeSpan throttle, Func<DateTime>? now = null)
    {
        _sessionsRoot = sessionsRoot;
        Throttle = throttle;
        Now = now ?? (() => DateTime.Now);
    }

    /// <summary>节流间隔：距上次扫描不足该间隔时，<see cref="Read"/> 直接返回缓存结果。</summary>
    public TimeSpan Throttle { get; }

    public Func<DateTime> Now { get; }

    /// <summary>累计真正发生的文件读取次数（用于诊断，不参与业务判断）。</summary>
    public int DiskReadCount { get; private set; }

    /// <summary>累计目录枚举次数。</summary>
    public int DirectoryScanCount { get; private set; }

    /// <summary>
    /// 清空缓存并让下一次 <see cref="Read"/> 立即重新扫描（供面板的刷新按钮调用）。
    /// 纯同步、只置状态，不安排任何定时器或后台任务。
    /// </summary>
    public void InvalidateCache()
    {
        _hasScanned = false;
        _lastScanAt = DateTime.MinValue;
        _cached = null;
    }

    public UsageSnapshot? Read()
    {
        var now = Now();
        if (_hasScanned && now - _lastScanAt < Throttle) return _cached;

        _lastScanAt = now;
        _hasScanned = true;
        _cached = null;

        foreach (var file in ListCandidates())
        {
            var snapshot = TryReadFile(file);
            if (snapshot is not null)
            {
                _cached = snapshot;
                break;
            }
        }

        return _cached;
    }

    private List<FileInfo> ListCandidates()
    {
        DirectoryScanCount++;

        if (!Directory.Exists(_sessionsRoot)) return [];

        try
        {
            // IgnoreInaccessible：遇到无权限的子目录时跳过继续枚举，不丢弃已找到的结果
            // AttributesToSkip=None：默认会跳过隐藏/系统目录，这里要与 SearchOption.AllDirectories 保持一致
            return new DirectoryInfo(_sessionsRoot)
                .EnumerateFiles("rollout-*.jsonl", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.None,
                })
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

                // ArrayPool<byte>.Shared 最大只到 2^20 字节：256 KB 的初始窗口是池化复用，
                // 4 MB 的扩展窗口会由 Rent 内部新建数组（不池化），无法避免一次 LOH 分配
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
