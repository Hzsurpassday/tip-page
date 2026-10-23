# Codex 额度悬浮看板 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 做一个 Windows 常驻托盘小工具：双击 Ctrl 在光标处弹出悬浮面板，显示 Codex 的 5 小时额度、本周额度、重置倒计时、额外点数。

**Architecture:** 单个 .NET 8 WPF 进程。`CodexUsageReader` 从 `~/.codex/sessions/**/rollout-*.jsonl` 的**文件尾部**提取最后一次 `payload.rate_limits` 快照；`DoubleCtrlHook` / `ClickAwayHook` 两个 Win32 低级钩子**在回调里把动作 Post 回 UI 线程**（不是轮询）；`PanelWindow` 用无边框置顶 + `WS_EX_NOACTIVATE` 渲染快照；`TrayManager` 负责常驻与开机自启。判定逻辑（解析、选窗规则、点击判定、文案格式化、位置换算）全部抽成纯函数，用 xUnit 覆盖；只有 Win32 壳与 XAML 走手动验收。

**资源策略（第一优先级）：** 常驻进程的空闲代价必须接近零——**隐藏状态下没有任何定时器、没有后台线程、不读文件**；只有用户双击 Ctrl / 点托盘 / 点刷新时才发生一次尾读（≤ 256 KB）。面板可见的 10 秒内才启动 1 秒心跳，且心跳只更新三段倒计时文本，不重建可视树、不做 IO。内存目标：框架依赖发布、工作集 ≤ 80 MB（目标 60 MB）。

**Tech Stack:** .NET 8 (`net8.0-windows`)、WPF、C# 12、xUnit、`Hardcodet.NotifyIcon.Wpf`（托盘）、`System.Drawing.Common`（托盘图标位图）

**Spec:** `E:\Viding\tip page\docs\superpowers\specs\2026-10-23-codex-usage-panel-design.md`

## Global Constraints

- 目标框架 `net8.0-windows`；`Nullable=enable`；`ImplicitUsings=enable`；WPF。
- 所有文件只能创建/修改在 `E:\Viding\tip page` 下。Bash 工具的 cwd 会被重置，命令一律 `cd "E:/Viding/tip page" && ...`。
- **禁止联网**：项目源码里不得出现 `HttpClient`、`WebClient`、`Socket`、`WebRequest`。
- **禁止读取 `~/.codex/auth.json`**。只读 `~/.codex/sessions/**/rollout-*.jsonl`。
- **隐私硬约束**：解析时只取 `payload.rate_limits` 子对象。异常/日志信息里只允许出现文件路径，**禁止**包含行内容——这些文件里有用户完整对话正文（实测单个文件 54 处 `input_text` 事件）。
- NuGet 只允许两个：`Hardcodet.NotifyIcon.Wpf`、`System.Drawing.Common`。不引入其他包。
- **资源硬约束（常驻工具，第一优先级）**：
  - 隐藏状态下**禁止**存在任何 `DispatcherTimer` / `System.Timers.Timer` / `Thread` / 后台轮询循环。允许常驻的只有两个低级钩子和一个托盘图标。
  - 禁止周期性后台刷新：只在用户唤出面板、点刷新按钮、点托盘菜单时读文件。
  - 面板可见期间的 1 秒心跳只允许改文本属性，**禁止**每秒重设 `DataContext`（会导致整个可视树重建）。
  - 尾读缓冲区用 `ArrayPool<byte>` 复用，禁止每次分配新数组。
  - 托盘图标只在百分比变化时重画，禁止每次刷新都新建 `Bitmap` / `Icon`。
  - 验收预算：空闲（面板隐藏）CPU 平均 **< 0.1%**、工作集 **≤ 80 MB**；面板可见 CPU **< 1%**；唤出到出数据 **< 150 ms**。
- 界面文案中文。所有时间按本地时区显示，并在界面标注"北京时间"。
- 提交身份取自 `E:\Viding\FDE-self-study\.qoder\env`（含 `GIT_AUTHOR_NAME/EMAIL`、`GIT_COMMITTER_NAME/EMAIL`）。**禁止**执行 `git config --global`、**禁止** `git config` 写任何身份。
- **禁止未经用户明确同意执行 `git push`**。
- 每个 Task 结束提交一次。提交命令统一为：

```bash
set -a && . "E:/Viding/FDE-self-study/.qoder/env" && set +a && \
git -C "E:/Viding/tip page" add <files> && \
git -C "E:/Viding/tip page" commit -m "<message>"
```

- 测试命令统一为：

```bash
cd "E:/Viding/tip page" && dotnet test CodexUsagePanel.sln --nologo
```

## File Structure

| 文件 | 职责 |
|---|---|
| `src/CodexUsagePanel/CodexUsagePanel.csproj` | WPF 可执行工程，net8.0-windows |
| `src/CodexUsagePanel/app.manifest` | PerMonitorV2 DPI 感知 |
| `src/CodexUsagePanel/App.xaml(.cs)` | 启动：单实例、组装配件、无 StartupUri |
| `src/CodexUsagePanel/Data/UsageSnapshot.cs` | 不可变快照模型 + 窗口派生属性 |
| `src/CodexUsagePanel/Data/RateLimitsParser.cs` | 单行 JSON → 快照（纯函数） |
| `src/CodexUsagePanel/Data/CodexUsageReader.cs` | 找文件 + 尾部窗口读取 + 重试 + 缓存节流 |
| `src/CodexUsagePanel/Settings/AppSettings.cs` | `%APPDATA%\CodexUsagePanel\settings.json` 读写 |
| `src/CodexUsagePanel/Input/CtrlTapDetector.cs` | 双击 Ctrl 判定（纯函数） |
| `src/CodexUsagePanel/Input/DoubleCtrlHook.cs` | Win32 键盘低级钩子，命中后 Post 到 UI 线程 |
| `src/CodexUsagePanel/Input/ClickAwayDetector.cs` | 点击是否落在面板外（纯函数） |
| `src/CodexUsagePanel/Input/ClickAwayHook.cs` | Win32 鼠标低级钩子，命中后 Post 到 UI 线程 |
| `src/CodexUsagePanel/Input/NativeInterop.cs` | 所有 P/Invoke 声明 |
| `src/CodexUsagePanel/Ui/PanelView.cs` | 快照 → 界面数据；时间相关属性走 INPC 增量更新 |
| `src/CodexUsagePanel/Ui/PanelPositioner.cs` | 物理像素 → DIP 坐标（纯函数） |
| `src/CodexUsagePanel/Ui/Theme.xaml` | 颜色/画刷资源 |
| `src/CodexUsagePanel/Ui/PanelWindow.xaml(.cs)` | 无边框置顶窗口、计时器、自动隐藏 |
| `src/CodexUsagePanel/Ui/Converters.cs` | `BoolToVisibilityConverter` |
| `src/CodexUsagePanel/Tray/TrayManager.cs` | 托盘图标与菜单、开机自启、图标绘制 |
| `src/CodexUsagePanel/Tray/AutoStart.cs` | 注册表 Run 项读写 |
| `tests/CodexUsagePanel.Tests/*` | xUnit，覆盖全部纯函数 |

---

### Task 1: 工程骨架 + 快照模型

**Files:**
- Create: `CodexUsagePanel.sln`、`src/CodexUsagePanel/CodexUsagePanel.csproj`、`src/CodexUsagePanel/App.xaml`、`src/CodexUsagePanel/App.xaml.cs`、`src/CodexUsagePanel/app.manifest`、`src/CodexUsagePanel/Ui/PanelWindow.xaml`、`src/CodexUsagePanel/Ui/PanelWindow.xaml.cs`、`src/CodexUsagePanel/Data/UsageSnapshot.cs`、`.gitattributes`
- Create: `tests/CodexUsagePanel.Tests/CodexUsagePanel.Tests.csproj`、`tests/CodexUsagePanel.Tests/Data/UsageSnapshotTests.cs`

**Interfaces:**
- Consumes: 无
- Produces: `CodexUsagePanel.Data.UsageWindow(double UsedPercent, int WindowMinutes, long ResetsAtUnixSeconds)`，派生属性 `IsFiveHour`、`IsWeekly`、`UsedPercentRounded`、`BarsFilled(int total = 20)`；`CodexUsagePanel.Data.UsageSnapshot(UsageWindow? Primary, UsageWindow? Secondary, string? CreditsBalance, string SourceFile, DateTime FileWrittenAt)`

- [ ] **Step 1: 创建解决方案与两个工程**

```bash
cd "E:/Viding/tip page" && \
dotnet new sln -n CodexUsagePanel && \
dotnet new wpf -o src/CodexUsagePanel -f net8.0 && \
dotnet new xunit -o tests/CodexUsagePanel.Tests -f net8.0 && \
dotnet sln add src/CodexUsagePanel/CodexUsagePanel.csproj tests/CodexUsagePanel.Tests/CodexUsagePanel.Tests.csproj && \
dotnet add tests/CodexUsagePanel.Tests/CodexUsagePanel.Tests.csproj reference src/CodexUsagePanel/CodexUsagePanel.csproj && \
dotnet add src/CodexUsagePanel/CodexUsagePanel.csproj package Hardcodet.NotifyIcon.Wpf && \
dotnet add src/CodexUsagePanel/CodexUsagePanel.csproj package System.Drawing.Common
```

- [ ] **Step 2: 删除模板生成的 `MainWindow.xaml(.cs)`，改用 `Ui/PanelWindow`**

```bash
cd "E:/Viding/tip page" && rm -f src/CodexUsagePanel/MainWindow.xaml src/CodexUsagePanel/MainWindow.xaml.cs
```

- [ ] **Step 3: 写 `app.manifest`（PerMonitorV2 DPI）**

创建 `src/CodexUsagePanel/app.manifest`：

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="CodexUsagePanel.app"/>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
    </windowsSettings>
  </application>
</assembly>
```

在 `CodexUsagePanel.csproj` 的 `<PropertyGroup>` 中加入以下几行（前一行是 DPI 清单，其余是常驻进程的内存策略）：

```xml
<ApplicationManifest>app.manifest</ApplicationManifest>
<ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>
<ServerGarbageCollection>false</ServerGarbageCollection>
<TieredPGO>true</TieredPGO>
```

`ConcurrentGarbageCollection=false` 去掉后台 GC 线程（少一条常驻线程、少量内存），对每秒只分配几个字符串的负载没有吞吐代价；`ServerGarbageCollection=false` 明确用工作站 GC。

- [ ] **Step 4: `App.xaml` 去掉 `StartupUri`（托盘启动，不自动开窗）**

```xml
<Application x:Class="CodexUsagePanel.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown">
    <Application.Resources/>
</Application>
```

- [ ] **Step 5: 写 `Data/UsageSnapshot.cs`**

```csharp
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
```

- [ ] **Step 6: 写失败的测试 `tests/CodexUsagePanel.Tests/Data/UsageSnapshotTests.cs`**

```csharp
using CodexUsagePanel.Data;
using Xunit;

namespace CodexUsagePanel.Tests.Data;

public class UsageSnapshotTests
{
    [Fact]
    public void FiveHourWindow_IsRecognisedByWindowMinutes()
    {
        var window = new UsageWindow(49.0, 300, 1792352158);
        Assert.True(window.IsFiveHour);
        Assert.False(window.IsWeekly);
    }

    [Fact]
    public void WeeklyWindow_IsRecognisedByWindowMinutes()
    {
        var window = new UsageWindow(32.0, 10080, 1792881761);
        Assert.True(window.IsWeekly);
        Assert.False(window.IsFiveHour);
    }

    [Theory]
    [InlineData(49.4, 49)]
    [InlineData(49.5, 50)]
    [InlineData(-3.0, 0)]
    [InlineData(140.0, 100)]
    public void UsedPercentRounded_ClampsAndRoundsAwayFromZero(double raw, int expected)
    {
        var window = new UsageWindow(raw, 300, 0);
        Assert.Equal(expected, window.UsedPercentRounded);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(49.0, 10)]
    [InlineData(68.0, 14)]
    [InlineData(100.0, 20)]
    public void BarsFilled_MapsPercentToTwentySegments(double raw, int expected)
    {
        var window = new UsageWindow(raw, 300, 0);
        Assert.Equal(expected, window.BarsFilled());
    }

    [Fact]
    public void Windows_EnumeratesOnlyNonNullEntries()
    {
        var onlyPrimary = new UsageSnapshot(new UsageWindow(10, 300, 0), null, null, "a", DateTime.UnixEpoch);
        Assert.Single(onlyPrimary.Windows);

        var both = new UsageSnapshot(new UsageWindow(10, 300, 0), new UsageWindow(20, 10080, 0), "0", "a", DateTime.UnixEpoch);
        Assert.Equal(2, both.Windows.Count());
    }
}
```

- [ ] **Step 7: 运行测试，确认通过**

```bash
cd "E:/Viding/tip page" && dotnet test CodexUsagePanel.sln --nologo
```

Expected: `Passed! - Failed: 0, Passed: 8`

- [ ] **Step 8: 写 `.gitattributes` 消除换行警告**

```
* text=auto
*.cs text eol=crlf
*.xaml text eol=crlf
*.csproj text eol=crlf
*.manifest text eol=crlf
```

- [ ] **Step 9: 提交**

```bash
set -a && . "E:/Viding/FDE-self-study/.qoder/env" && set +a && \
git -C "E:/Viding/tip page" add .gitattributes CodexUsagePanel.sln src tests && \
git -C "E:/Viding/tip page" commit -m "feat: WPF 工程骨架与用量快照模型"
```

---

### Task 2: `rate_limits` 行解析（纯函数）

**Files:**
- Create: `src/CodexUsagePanel/Data/RateLimitsParser.cs`
- Create: `tests/CodexUsagePanel.Tests/Data/RateLimitsParserTests.cs`

**Interfaces:**
- Consumes: `UsageSnapshot`、`UsageWindow`
- Produces: `RateLimitsParser.ParseLines(IEnumerable<string> lines, string sourceFile, DateTime writtenAt) -> UsageSnapshot?`

- [ ] **Step 1: 写失败的测试**

```csharp
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
```

- [ ] **Step 2: 运行测试确认失败**

```bash
cd "E:/Viding/tip page" && dotnet test CodexUsagePanel.sln --nologo --filter "FullyQualifiedName~RateLimitsParserTests"
```

Expected: 编译错误 `The name 'RateLimitsParser' does not exist`

- [ ] **Step 3: 实现 `Data/RateLimitsParser.cs`**

```csharp
using System.Text.Json;

namespace CodexUsagePanel.Data;

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
            if (line.Length == 0 || line.IndexOf(Marker, StringComparison.Ordinal) < 0) continue;

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
            // 只报告文件路径，绝不输出行内容——行里含用户对话正文
            return null;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("payload", out var payload)) return null;
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

        int minutes = window.TryGetProperty("window_minutes", out var wm) && wm.ValueKind == JsonValueKind.Number
            ? wm.GetInt32()
            : 0;

        long resetsAt = window.TryGetProperty("resets_at", out var ra) && ra.ValueKind == JsonValueKind.Number
            ? ra.GetInt64()
            : 0;

        return new UsageWindow(used.GetDouble(), minutes, resetsAt);
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
```

- [ ] **Step 4: 运行测试确认通过**

```bash
cd "E:/Viding/tip page" && dotnet test CodexUsagePanel.sln --nologo --filter "FullyQualifiedName~RateLimitsParserTests"
```

Expected: `Passed! - Failed: 0, Passed: 7`

- [ ] **Step 5: 提交**

```bash
set -a && . "E:/Viding/FDE-self-study/.qoder/env" && set +a && \
git -C "E:/Viding/tip page" add src/CodexUsagePanel/Data/RateLimitsParser.cs tests && \
git -C "E:/Viding/tip page" commit -m "feat: 解析 Codex session 里的 rate_limits 快照"
```

---

### Task 3: 文件发现 + 尾部窗口读取

**Files:**
- Create: `src/CodexUsagePanel/Data/CodexUsageReader.cs`
- Create: `tests/CodexUsagePanel.Tests/Data/CodexUsageReaderFileTests.cs`

**Interfaces:**
- Consumes: `RateLimitsParser.ParseLines`
- Produces: `CodexUsageReader(string sessionsRoot, TimeSpan throttle, Func<DateTime>? now = null)`；`UsageSnapshot? Read()`；只读属性 `int DiskReadCount`、`int DirectoryScanCount`

- [ ] **Step 1: 写失败的测试**

```csharp
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

    private static string Padding(int chars) =>
        """{"timestamp":"t","type":"response_item","payload":{"type":"message","text":"""" + new string('x', chars) + "\"}}\n";

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
```

- [ ] **Step 2: 运行测试确认失败**

```bash
cd "E:/Viding/tip page" && dotnet test CodexUsagePanel.sln --nologo --filter "FullyQualifiedName~CodexUsageReaderFileTests"
```

Expected: 编译错误 `The name 'CodexUsageReader' does not exist`

- [ ] **Step 3: 实现 `Data/CodexUsageReader.cs`（本任务只做找文件+尾部读取，缓存留给 Task 4）**

```csharp
using System.Buffers;
using System.Text;

namespace CodexUsagePanel.Data;

public sealed class CodexUsageReader
{
    private const int InitialTailBytes = 256 * 1024;
    private const int ExpandedTailBytes = 4 * 1024 * 1024;
    private const int MaxCandidateFiles = 3;

    private readonly string _sessionsRoot;

    public CodexUsageReader(string sessionsRoot, TimeSpan throttle, Func<DateTime>? now = null)
    {
        _sessionsRoot = sessionsRoot;
        Throttle = throttle;
        Now = now ?? (() => DateTime.Now);
    }

    public TimeSpan Throttle { get; }

    public Func<DateTime> Now { get; }

    public int DiskReadCount { get; private set; }

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
        var writtenAt = file.LastWriteTime;
        DateTime lastWrite;

        try
        {
            lastWrite = file.LastWriteTimeUtc;
        }
        catch (IOException)
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

    private List<string> ReadTailLines(string path, int maxBytes, out bool reachedStart)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
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
                Thread.Sleep(50);
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
```

- [ ] **Step 4: 运行测试确认通过**

```bash
cd "E:/Viding/tip page" && dotnet test CodexUsagePanel.sln --nologo --filter "FullyQualifiedName~CodexUsageReaderFileTests"
```

Expected: `Passed! - Failed: 0, Passed: 6`

- [ ] **Step 5: 提交**

```bash
set -a && . "E:/Viding/FDE-self-study/.qoder/env" && set +a && \
git -C "E:/Viding/tip page" add src/CodexUsagePanel/Data/CodexUsageReader.cs tests && \
git -C "E:/Viding/tip page" commit -m "feat: 定位最新 session 文件并只读尾部窗口"
```

---

### Task 4: 缓存与节流

**Files:**
- Modify: `src/CodexUsagePanel/Data/CodexUsageReader.cs`
- Create: `tests/CodexUsagePanel.Tests/Data/CodexUsageReaderCacheTests.cs`

**Interfaces:**
- Consumes: `CodexUsageReader.Read()`、`DiskReadCount`、`DirectoryScanCount`
- Produces: 行为不变，新增 `InvalidateCache()` 供面板刷新按钮调用

- [ ] **Step 1: 写失败的测试**

```csharp
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
```

- [ ] **Step 2: 运行测试确认失败**

```bash
cd "E:/Viding/tip page" && dotnet test CodexUsagePanel.sln --nologo --filter "FullyQualifiedName~CodexUsageReaderCacheTests"
```

Expected: `AfterThrottleExpires_FileIsReadAgain` 与 `InvalidateCache_ForcesRescan` 失败

- [ ] **Step 3: 给 `Read()` 加上缓存与节流**

把 `Read()` 替换为：

```csharp
    private UsageSnapshot? _cached;
    private DateTime _lastScanAt = DateTime.MinValue;
    private bool _hasScanned;

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
```

同时删除原来那个直接遍历的 `Read()` 实现。

- [ ] **Step 4: 运行全部测试确认通过**

```bash
cd "E:/Viding/tip page" && dotnet test CodexUsagePanel.sln --nologo
```

Expected: `Passed! - Failed: 0`，共 25 个测试

- [ ] **Step 5: 提交**

```bash
set -a && . "E:/Viding/FDE-self-study/.qoder/env" && set +a && \
git -C "E:/Viding/tip page" add src/CodexUsagePanel/Data/CodexUsageReader.cs tests && \
git -C "E:/Viding/tip page" commit -m "feat: 用量读取加 15 秒节流与快照缓存"
```

---

### Task 5: 设置读写

**Files:**
- Create: `src/CodexUsagePanel/Settings/AppSettings.cs`
- Create: `tests/CodexUsagePanel.Tests/Settings/AppSettingsTests.cs`

**Interfaces:**
- Consumes: 无
- Produces: `AppSettings`（可写属性 `DoubleCtrlWindowMs=400`、`AutoHideSeconds=10`、`RefreshThrottleSeconds=15`、`PlanLabel="PLUS"`、`FixedPositionEnabled=false`、`FixedPositionX=0`、`FixedPositionY=0`）；`AppSettings.Load(string path)`、`AppSettings.Save(string path)`、`static string DefaultPath()`

- [ ] **Step 1: 写失败的测试**

```csharp
using CodexUsagePanel.Settings;
using Xunit;

namespace CodexUsagePanel.Tests.Settings;

public class AppSettingsTests
{
    [Fact]
    public void MissingFile_YieldsDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var settings = AppSettings.Load(path);

        Assert.Equal(400, settings.DoubleCtrlWindowMs);
        Assert.Equal(10, settings.AutoHideSeconds);
        Assert.Equal(15, settings.RefreshThrottleSeconds);
        Assert.Equal("PLUS", settings.PlanLabel);
        Assert.False(settings.FixedPositionEnabled);
    }

    [Fact]
    public void RoundTripsThroughDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "settings.json");

        var settings = AppSettings.Load(path);
        settings.AutoHideSeconds = 3;
        settings.PlanLabel = "PRO";
        settings.FixedPositionEnabled = true;
        settings.FixedPositionX = 100;
        settings.FixedPositionY = 200;
        settings.Save(path);

        var reloaded = AppSettings.Load(path);
        Assert.Equal(3, reloaded.AutoHideSeconds);
        Assert.Equal("PRO", reloaded.PlanLabel);
        Assert.True(reloaded.FixedPositionEnabled);
        Assert.Equal(100, reloaded.FixedPositionX);
        Assert.Equal(200, reloaded.FixedPositionY);
    }

    [Fact]
    public void CorruptFile_IsBackedUpAndDefaultsRestored()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{ this is not json");

        var settings = AppSettings.Load(path);

        Assert.Equal(400, settings.DoubleCtrlWindowMs);
        Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public void OutOfRangeValues_AreClamped()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, """{"DoubleCtrlWindowMs":5,"AutoHideSeconds":9999}""");

        var settings = AppSettings.Load(path);

        Assert.Equal(150, settings.DoubleCtrlWindowMs);
        Assert.Equal(120, settings.AutoHideSeconds);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
cd "E:/Viding/tip page" && dotnet test CodexUsagePanel.sln --nologo --filter "FullyQualifiedName~AppSettingsTests"
```

Expected: 编译错误 `The type or namespace name 'AppSettings' could not be found`

- [ ] **Step 3: 实现 `Settings/AppSettings.cs`**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexUsagePanel.Settings;

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public int DoubleCtrlWindowMs { get; set; } = 400;

    public int AutoHideSeconds { get; set; } = 10;

    public int RefreshThrottleSeconds { get; set; } = 15;

    public string PlanLabel { get; set; } = "PLUS";

    public bool FixedPositionEnabled { get; set; }

    public int FixedPositionX { get; set; }

    public int FixedPositionY { get; set; }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CodexUsagePanel",
        "settings.json");

    public static AppSettings Load(string path)
    {
        AppSettings settings;

        try
        {
            if (!File.Exists(path)) return new AppSettings();
            var json = File.ReadAllText(path);
            settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
            TryBackup(path);
            return new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }

        settings.Normalize();
        return settings;
    }

    public void Save(string path)
    {
        Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    private void Normalize()
    {
        DoubleCtrlWindowMs = Math.Clamp(DoubleCtrlWindowMs, 150, 1000);
        AutoHideSeconds = Math.Clamp(AutoHideSeconds, 1, 120);
        RefreshThrottleSeconds = Math.Clamp(RefreshThrottleSeconds, 5, 300);
        PlanLabel = string.IsNullOrWhiteSpace(PlanLabel) ? "PLUS" : PlanLabel.Trim();
    }

    private static void TryBackup(string path)
    {
        try
        {
            File.Copy(path, path + ".bak", overwrite: true);
        }
        catch (IOException)
        {
        }
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

```bash
cd "E:/Viding/tip page" && dotnet test CodexUsagePanel.sln --nologo --filter "FullyQualifiedName~AppSettingsTests"
```

Expected: `Passed! - Failed: 0, Passed: 4`

- [ ] **Step 5: 提交**

```bash
set -a && . "E:/Viding/FDE-self-study/.qoder/env" && set +a && \
git -C "E:/Viding/tip page" add src/CodexUsagePanel/Settings tests && \
git -C "E:/Viding/tip page" commit -m "feat: 配置读写，损坏自动备份并回落默认值"
```

---

### Task 6: 双击 Ctrl 判定 + 键盘钩子

**Files:**
- Create: `src/CodexUsagePanel/Input/CtrlTapDetector.cs`、`src/CodexUsagePanel/Input/NativeInterop.cs`、`src/CodexUsagePanel/Input/DoubleCtrlHook.cs`
- Create: `tests/CodexUsagePanel.Tests/Input/CtrlTapDetectorTests.cs`

**Interfaces:**
- Consumes: 无
- Produces: `CtrlTapDetector(int windowMs)`；`bool Feed(bool isDown, bool isRepeat, long nowMs)`。`DoubleCtrlHook(int windowMs, Func<bool> isPanelVisible, Action onToggle, Action onEscape)`；`Start()`、`Stop()`。命中后由钩子自己 `Post` 到创建它时所在的 UI 线程，调用方不需要任何轮询。

- [ ] **Step 1: 写失败的测试**

```csharp
using CodexUsagePanel.Input;
using Xunit;

namespace CodexUsagePanel.Tests.Input;

public class CtrlTapDetectorTests
{
    [Fact]
    public void SinglePress_DoesNotTrigger()
    {
        var detector = new CtrlTapDetector(400);
        Assert.False(detector.Feed(isDown: true, isRepeat: false, nowMs: 1000));
        Assert.False(detector.Feed(isDown: false, isRepeat: false, nowMs: 1050));
    }

    [Fact]
    public void TwoQuickPresses_Trigger()
    {
        var detector = new CtrlTapDetector(400);
        detector.Feed(true, false, 1000);
        detector.Feed(false, false, 1040);
        Assert.True(detector.Feed(true, false, 1100));
    }

    [Fact]
    public void TwoPressesBeyondWindow_DoNotTrigger()
    {
        var detector = new CtrlTapDetector(400);
        detector.Feed(true, false, 1000);
        detector.Feed(false, false, 1040);
        Assert.False(detector.Feed(true, false, 1500));
    }

    [Fact]
    public void AutoRepeatWhileHeld_IsIgnored()
    {
        var detector = new CtrlTapDetector(400);
        detector.Feed(true, false, 1000);
        for (int i = 1; i <= 30; i++)
        {
            Assert.False(detector.Feed(true, isRepeat: true, nowMs: 1000 + i * 30));
        }
    }

    [Fact]
    public void ThirdPressAfterTrigger_DoesNotRetriggerImmediately()
    {
        var detector = new CtrlTapDetector(400);
        detector.Feed(true, false, 1000);
        detector.Feed(false, false, 1020);
        Assert.True(detector.Feed(true, false, 1050));
        detector.Feed(false, false, 1070);
        Assert.False(detector.Feed(true, false, 1100));
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
cd "E:/Viding/tip page" && dotnet test CodexUsagePanel.sln --nologo --filter "FullyQualifiedName~CtrlTapDetectorTests"
```

Expected: 编译错误 `The type or namespace name 'CtrlTapDetector' could not be found`

- [ ] **Step 3: 实现 `Input/CtrlTapDetector.cs`**

```csharp
namespace CodexUsagePanel.Input;

public sealed class CtrlTapDetector
{
    private readonly int _windowMs;
    private long _lastTapAtMs = long.MinValue;
    private bool _ctrlHeld;

    public CtrlTapDetector(int windowMs) => _windowMs = windowMs;

    public bool Feed(bool isDown, bool isRepeat, long nowMs)
    {
        if (!isDown)
        {
            _ctrlHeld = false;
            return false;
        }

        // 长按期间的自动重复不是新的"按下"
        if (_ctrlHeld || isRepeat) return false;

        _ctrlHeld = true;

        if (nowMs - _lastTapAtMs <= _windowMs)
        {
            _lastTapAtMs = long.MinValue;   // 消费掉这次配对，避免连按三下再触发
            return true;
        }

        _lastTapAtMs = nowMs;
        return false;
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

```bash
cd "E:/Viding/tip page" && dotnet test CodexUsagePanel.sln --nologo --filter "FullyQualifiedName~CtrlTapDetectorTests"
```

Expected: `Passed! - Failed: 0, Passed: 5`

- [ ] **Step 5: 写 `Input/NativeInterop.cs`**

```csharp
using System.Runtime.InteropServices;

namespace CodexUsagePanel.Input;

internal static class NativeInterop
{
    internal const int WH_KEYBOARD_LL = 13;
    internal const int WH_MOUSE_LL = 14;

    internal const int WM_KEYDOWN = 0x0100;
    internal const int WM_KEYUP = 0x0101;
    internal const int WM_SYSKEYDOWN = 0x0104;
    internal const int WM_SYSKEYUP = 0x0105;

    internal const int WM_LBUTTONDOWN = 0x0201;
    internal const int WM_RBUTTONDOWN = 0x0204;
    internal const int WM_MBUTTONDOWN = 0x0207;

    internal const uint LLKHF_UP = 0x0080;
    internal const int VK_CONTROL = 0x11;
    internal const int VK_ESCAPE = 0x1B;

    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_NOACTIVATE = 0x08000000;

    internal const uint MONITOR_DEFAULTTONEAREST = 2;

    internal delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc callback, IntPtr hInstance, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hookId);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hookId, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    internal static extern IntPtr GetWindowLongPtr32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    internal static extern IntPtr SetWindowLongPtr32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    internal static IntPtr GetWindowLongPtr(IntPtr hWnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, index) : GetWindowLongPtr32(hWnd, index);

    internal static IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, index, value) : SetWindowLongPtr32(hWnd, index, value);

    internal static RECT GetWorkAreaNear(int screenX, int screenY)
    {
        var monitor = MonitorFromPoint(new POINT { X = screenX, Y = screenY }, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(monitor, ref info) ? info.rcWork : new RECT { Right = 1920, Bottom = 1080 };
    }

    internal static POINT GetCursorPosition()
    {
        GetCursorPos(out var point);
        return point;
    }
}
```

- [ ] **Step 6: 写 `Input/DoubleCtrlHook.cs`（Win32 壳，不写自动化测试）**

```csharp
using System.Runtime.InteropServices;

namespace CodexUsagePanel.Input;

public sealed class DoubleCtrlHook : IDisposable
{
    private readonly CtrlTapDetector _detector;
    private readonly Func<bool> _isPanelVisible;
    private readonly Action _onToggle;
    private readonly Action _onEscape;
    private readonly SynchronizationContext? _uiContext;
    private readonly NativeInterop.LowLevelProc _callback;   // 必须持有引用，防止被 GC 回收
    private IntPtr _handle = IntPtr.Zero;

    public DoubleCtrlHook(int windowMs, Func<bool> isPanelVisible, Action onToggle, Action onEscape)
    {
        _detector = new CtrlTapDetector(windowMs);
        _isPanelVisible = isPanelVisible;
        _onToggle = onToggle;
        _onEscape = onEscape;
        _uiContext = SynchronizationContext.Current;   // 在 UI 线程构造，后续用它 Post
        _callback = Handle;
    }

    public bool Start()
    {
        if (_handle != IntPtr.Zero) return true;

        _handle = NativeInterop.SetWindowsHookEx(
            NativeInterop.WH_KEYBOARD_LL,
            _callback,
            NativeInterop.GetModuleHandle(null),
            0);

        return _handle != IntPtr.Zero;
    }

    public void Stop()
    {
        if (_handle == IntPtr.Zero) return;
        NativeInterop.UnhookWindowsHookEx(_handle);
        _handle = IntPtr.Zero;
    }

    public void Dispose() => Stop();

    private IntPtr Handle(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            bool isUp = wParam == NativeInterop.WM_KEYUP || wParam == NativeInterop.WM_SYSKEYUP;

            if (isUp || wParam == NativeInterop.WM_KEYDOWN || wParam == NativeInterop.WM_SYSKEYDOWN)
            {
                var data = Marshal.PtrToStructure<NativeInterop.KBDLLHOOKSTRUCT>(lParam);

                if (data.vkCode == NativeInterop.VK_CONTROL)
                {
                    if (_detector.Feed(isDown: !isUp, isRepeat: false, nowMs: Environment.TickCount64))
                    {
                        Post(_onToggle);
                    }
                }
                else if (data.vkCode == NativeInterop.VK_ESCAPE && !isUp && _isPanelVisible())
                {
                    Post(_onEscape);
                }
            }
        }

        // 回调预算约 300ms，超时 Windows 会静默卸载钩子。这里只做 Post，不做 IO / UI。
        return NativeInterop.CallNextHookEx(_handle, nCode, wParam, lParam);
    }

    private void Post(Action action)
    {
        if (_uiContext is null)
        {
            action();
            return;
        }

        _uiContext.Post(static state => ((Action)state!)(), action);
    }
}
```

**为什么不用轮询：** 低级钩子的回调本身就在安装钩子的线程（也就是 WPF 的 UI 线程）的消息循环里执行，所以可以直接 `Post` 回队列，下一个消息就被处理。这比 80 ms 轮询定时器快一个数量级，而且程序空闲时 CPU 占用严格为 0（轮询定时器每秒会唤醒 12.5 次）。`Post` 只排一个委托，回调本身仍然是微秒级。

**每次按键的代价：** 键盘钩子会对全系统的每一次按键调用一次回调，开销是一次结构体封送加两个整数比较（微秒级），不分配堆内存。这是全局"双击 Ctrl"热键无法避免的成本；如果以后改成 `RegisterHotKey` 的组合键，可以完全去掉这条路径。

- [ ] **Step 7: 编译并跑全部测试**

```bash
cd "E:/Viding/tip page" && dotnet build CodexUsagePanel.sln --nologo && dotnet test CodexUsagePanel.sln --nologo
```

Expected: 构建成功；`Passed! - Failed: 0`

- [ ] **Step 8: 提交**

```bash
set -a && . "E:/Viding/FDE-self-study/.qoder/env" && set +a && \
git -C "E:/Viding/tip page" add src/CodexUsagePanel/Input tests && \
git -C "E:/Viding/tip page" commit -m "feat: 双击 Ctrl 判定与全局键盘钩子"
```

---

### Task 7: 点击外部关闭判定 + 鼠标钩子

**Files:**
- Create: `src/CodexUsagePanel/Input/ClickAwayDetector.cs`、`src/CodexUsagePanel/Input/ClickAwayHook.cs`
- Create: `tests/CodexUsagePanel.Tests/Input/ClickAwayDetectorTests.cs`

**Interfaces:**
- Consumes: `NativeInterop`
- Produces: `ClickAwayDetector.IsOutside(int x, int y, int left, int top, int right, int bottom) -> bool`；`ClickAwayHook(Func<(int Left,int Top,int Right,int Bottom)?> boundsProvider, Action onClickAway)`、`Start()`、`Stop()`。命中后由钩子自己 `Post` 到 UI 线程。

- [ ] **Step 1: 写失败的测试**

```csharp
using CodexUsagePanel.Input;
using Xunit;

namespace CodexUsagePanel.Tests.Input;

public class ClickAwayDetectorTests
{
    [Theory]
    [InlineData(500, 300, false)]   // 内部
    [InlineData(100, 100, false)]   // 左上角边界
    [InlineData(820, 520, false)]   // 右下角边界
    [InlineData(99, 300, true)]     // 左侧外
    [InlineData(821, 300, true)]    // 右侧外
    [InlineData(500, 99, true)]     // 上方外
    [InlineData(500, 521, true)]    // 下方外
    public void IsOutside_UsesInclusiveBounds(int x, int y, bool expected)
    {
        Assert.Equal(expected, ClickAwayDetector.IsOutside(x, y, 100, 100, 820, 520));
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
cd "E:/Viding/tip page" && dotnet test CodexUsagePanel.sln --nologo --filter "FullyQualifiedName~ClickAwayDetectorTests"
```

Expected: 编译错误 `The name 'ClickAwayDetector' does not exist`

- [ ] **Step 3: 实现 `Input/ClickAwayDetector.cs`**

```csharp
namespace CodexUsagePanel.Input;

public static class ClickAwayDetector
{
    public static bool IsOutside(int x, int y, int left, int top, int right, int bottom) =>
        x < left || x > right || y < top || y > bottom;
}
```

- [ ] **Step 4: 运行测试确认通过**

```bash
cd "E:/Viding/tip page" && dotnet test CodexUsagePanel.sln --nologo --filter "FullyQualifiedName~ClickAwayDetectorTests"
```

Expected: `Passed! - Failed: 0, Passed: 7`

- [ ] **Step 5: 实现 `Input/ClickAwayHook.cs`**

```csharp
using System.Runtime.InteropServices;

namespace CodexUsagePanel.Input;

public sealed class ClickAwayHook : IDisposable
{
    private readonly Func<(int Left, int Top, int Right, int Bottom)?> _boundsProvider;
    private readonly Action _onClickAway;
    private readonly SynchronizationContext? _uiContext;
    private readonly NativeInterop.LowLevelProc _callback;
    private IntPtr _handle = IntPtr.Zero;

    public ClickAwayHook(Func<(int Left, int Top, int Right, int Bottom)?> boundsProvider, Action onClickAway)
    {
        _boundsProvider = boundsProvider;
        _onClickAway = onClickAway;
        _uiContext = SynchronizationContext.Current;   // 在 UI 线程构造
        _callback = Handle;
    }

    public void Start()
    {
        if (_handle != IntPtr.Zero) return;
        _handle = NativeInterop.SetWindowsHookEx(
            NativeInterop.WH_MOUSE_LL,
            _callback,
            NativeInterop.GetModuleHandle(null),
            0);
    }

    public void Stop()
    {
        if (_handle == IntPtr.Zero) return;
        NativeInterop.UnhookWindowsHookEx(_handle);
        _handle = IntPtr.Zero;
    }

    public void Dispose() => Stop();

    private IntPtr Handle(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int message = (int)wParam;
            if (message is NativeInterop.WM_LBUTTONDOWN
                        or NativeInterop.WM_RBUTTONDOWN
                        or NativeInterop.WM_MBUTTONDOWN)
            {
                var bounds = _boundsProvider();
                if (bounds is { } b)
                {
                    var data = Marshal.PtrToStructure<NativeInterop.MSLLHOOKSTRUCT>(lParam);
                    if (ClickAwayDetector.IsOutside(data.pt.X, data.pt.Y, b.Left, b.Top, b.Right, b.Bottom))
                    {
                        if (_uiContext is null) _onClickAway();
                        else _uiContext.Post(static state => ((Action)state!)(), _onClickAway);
                    }
                }
            }
        }

        return NativeInterop.CallNextHookEx(_handle, nCode, wParam, lParam);
    }
}
```

- [ ] **Step 6: 编译并跑测试**

```bash
cd "E:/Viding/tip page" && dotnet build CodexUsagePanel.sln --nologo && dotnet test CodexUsagePanel.sln --nologo
```

Expected: 构建成功；`Passed! - Failed: 0`

- [ ] **Step 7: 提交**

```bash
set -a && . "E:/Viding/FDE-self-study/.qoder/env" && set +a && \
git -C "E:/Viding/tip page" add src/CodexUsagePanel/Input tests && \
git -C "E:/Viding/tip page" commit -m "feat: 点击面板外关闭的判定与鼠标钩子"
```

---

### Task 8: 界面数据映射

**Files:**
- Create: `src/CodexUsagePanel/Ui/PanelView.cs`
- Create: `tests/CodexUsagePanel.Tests/Ui/PanelViewTests.cs`

**Interfaces:**
- Consumes: `UsageSnapshot`、`UsageWindow`、`AppSettings`
- Produces: `CardView`（`INotifyPropertyChanged`，含 `Tick(DateTime)`）、`BarView`、`PanelView`（`INotifyPropertyChanged`，含 `Tick(DateTime)`）、`static PanelView.Build(UsageSnapshot? snapshot, DateTime now, string planLabel)`、`internal static PanelView.FormatCountdown(TimeSpan)`

- [ ] **Step 1: 写失败的测试**

```csharp
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
```

- [ ] **Step 2: 运行测试确认失败**

```bash
cd "E:/Viding/tip page" && dotnet test CodexUsagePanel.sln --nologo --filter "FullyQualifiedName~PanelViewTests"
```

Expected: 编译错误 `The type or namespace name 'PanelView' could not be found`

- [ ] **Step 3: 实现 `Ui/PanelView.cs`**

```csharp
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
```

- [ ] **Step 4: 运行测试确认通过**

```bash
cd "E:/Viding/tip page" && dotnet test CodexUsagePanel.sln --nologo --filter "FullyQualifiedName~PanelViewTests"
```

Expected: `Passed! - Failed: 0, Passed: 19`（16 个 `[Fact]` + 1 个 3 用例的 `[Theory]`）

- [ ] **Step 5: 提交**

```bash
set -a && . "E:/Viding/FDE-self-study/.qoder/env" && set +a && \
git -C "E:/Viding/tip page" add src/CodexUsagePanel/Ui/PanelView.cs tests && \
git -C "E:/Viding/tip page" commit -m "feat: 快照到界面文案的映射，含倒计时与同步状态"
```

---

### Task 9: 窗口位置计算 + 主题 + 面板窗口

**Files:**
- Create: `src/CodexUsagePanel/Ui/PanelPositioner.cs`、`src/CodexUsagePanel/Ui/Converters.cs`、`src/CodexUsagePanel/Ui/Theme.xaml`
- Modify: `src/CodexUsagePanel/Ui/PanelWindow.xaml`、`src/CodexUsagePanel/Ui/PanelWindow.xaml.cs`
- Create: `tests/CodexUsagePanel.Tests/Ui/PanelPositionerTests.cs`

**Interfaces:**
- Consumes: `PanelView`、`NativeInterop`
- Produces: `PanelPositioner.Resolve(int cursorX, int cursorY, int workLeft, int workTop, int workRight, int workBottom, double dpiScale, double logicalWidth, double logicalHeight, int offset) -> (double Left, double Top)`；`PanelWindow` 暴露 `ShowPanel(PanelView view)`、`HidePanel()`、`CurrentBoundsPhysical`、事件 `RefreshRequested`、`SettingsRequested`

- [ ] **Step 1: 写失败的测试**

```csharp
using CodexUsagePanel.Ui;
using Xunit;

namespace CodexUsagePanel.Tests.Ui;

public class PanelPositionerTests
{
    [Fact]
    public void PlacesPanelOffsetBelowRightOfCursor()
    {
        var (left, top) = PanelPositioner.Resolve(
            cursorX: 100, cursorY: 100,
            workLeft: 0, workTop: 0, workRight: 1920, workBottom: 1040,
            dpiScale: 1.0, logicalWidth: 720, logicalHeight: 420, offset: 16);

        Assert.Equal(116, left);
        Assert.Equal(116, top);
    }

    [Fact]
    public void ClampsInsideWorkAreaAtBottomRightCorner()
    {
        var (left, top) = PanelPositioner.Resolve(
            cursorX: 1900, cursorY: 1030,
            workLeft: 0, workTop: 0, workRight: 1920, workBottom: 1040,
            dpiScale: 1.0, logicalWidth: 720, logicalHeight: 420, offset: 16);

        Assert.Equal(1200, left);
        Assert.Equal(620, top);
    }

    [Fact]
    public void ConvertsPhysicalPixelsToDipsAtOneHundredFiftyPercent()
    {
        var (left, top) = PanelPositioner.Resolve(
            cursorX: 150, cursorY: 300,
            workLeft: 0, workTop: 0, workRight: 2880, workBottom: 1620,
            dpiScale: 1.5, logicalWidth: 720, logicalHeight: 420, offset: 16);

        Assert.Equal(166 / 1.5, left, 3);
        Assert.Equal(316 / 1.5, top, 3);
    }

    [Fact]
    public void HandlesSecondaryMonitorWithNegativeCoordinates()
    {
        var (left, top) = PanelPositioner.Resolve(
            cursorX: -1900, cursorY: 1020,
            workLeft: -1920, workTop: 0, workRight: 0, workBottom: 1040,
            dpiScale: 1.0, logicalWidth: 720, logicalHeight: 420, offset: 16);

        Assert.Equal(-1884, left);   // -1900 + 16，右侧空间足够，不触发夹回
        Assert.Equal(620, top);      // 1036 + 420 越界 → 1040 - 420
    }

    [Fact]
    public void ClampsToWorkAreaLeftEdge()
    {
        var (left, _) = PanelPositioner.Resolve(
            cursorX: -1900, cursorY: 100,
            workLeft: -1200, workTop: 0, workRight: 0, workBottom: 1040,
            dpiScale: 1.0, logicalWidth: 720, logicalHeight: 420, offset: 16);

        Assert.Equal(-1200, left);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
cd "E:/Viding/tip page" && dotnet test CodexUsagePanel.sln --nologo --filter "FullyQualifiedName~PanelPositionerTests"
```

Expected: 编译错误 `The name 'PanelPositioner' does not exist`

- [ ] **Step 3: 实现 `Ui/PanelPositioner.cs`**

```csharp
namespace CodexUsagePanel.Ui;

public static class PanelPositioner
{
    public static (double Left, double Top) Resolve(
        int cursorX,
        int cursorY,
        int workLeft,
        int workTop,
        int workRight,
        int workBottom,
        double dpiScale,
        double logicalWidth,
        double logicalHeight,
        int offset = 16)
    {
        double width = logicalWidth * dpiScale;
        double height = logicalHeight * dpiScale;

        double left = cursorX + offset;
        double top = cursorY + offset;

        if (left + width > workRight) left = workRight - width;
        if (top + height > workBottom) top = workBottom - height;
        if (left < workLeft) left = workLeft;
        if (top < workTop) top = workTop;

        return (left / dpiScale, top / dpiScale);
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

```bash
cd "E:/Viding/tip page" && dotnet test CodexUsagePanel.sln --nologo --filter "FullyQualifiedName~PanelPositionerTests"
```

Expected: `Passed! - Failed: 0, Passed: 4`

- [ ] **Step 5: 写 `Ui/Theme.xaml`**

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Color x:Key="BgColor">#0E1416</Color>
    <Color x:Key="CardColor">#131B1E</Color>
    <Color x:Key="LineColor">#1E2A2E</Color>
    <Color x:Key="TextColor">#E8F0EE</Color>
    <Color x:Key="MutedColor">#6B8080</Color>
    <Color x:Key="MintColor">#4ADEB4</Color>
    <Color x:Key="MistColor">#7EB3F0</Color>
    <Color x:Key="AmberColor">#F0A860</Color>
    <Color x:Key="CoralColor">#EF6B6B</Color>

    <SolidColorBrush x:Key="BgBrush" Color="{StaticResource BgColor}"/>
    <SolidColorBrush x:Key="CardBrush" Color="{StaticResource CardColor}"/>
    <SolidColorBrush x:Key="LineBrush" Color="{StaticResource LineColor}"/>
    <SolidColorBrush x:Key="TextBrush" Color="{StaticResource TextColor}"/>
    <SolidColorBrush x:Key="MutedBrush" Color="{StaticResource MutedColor}"/>
    <SolidColorBrush x:Key="MintBrush" Color="{StaticResource MintColor}"/>
    <SolidColorBrush x:Key="MistBrush" Color="{StaticResource MistColor}"/>
    <SolidColorBrush x:Key="AmberBrush" Color="{StaticResource AmberColor}"/>
    <SolidColorBrush x:Key="CoralBrush" Color="{StaticResource CoralColor}"/>
    <SolidColorBrush x:Key="DimBarBrush" Color="#223034"/>
</ResourceDictionary>
```

- [ ] **Step 6: 写 `Ui/Converters.cs`**

```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CodexUsagePanel.Ui;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value is true;
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
```

- [ ] **Step 7: 写 `Ui/PanelWindow.xaml`**

```xml
<Window x:Class="CodexUsagePanel.Ui.PanelWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:ui="clr-namespace:CodexUsagePanel.Ui"
        Title="Codex 额度看板"
        Width="720" Height="420"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        ResizeMode="NoResize" Topmost="True" ShowInTaskbar="False" ShowActivated="False"
        WindowStartupLocation="Manual" FontFamily="Microsoft YaHei UI">
    <Window.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="Theme.xaml"/>
            </ResourceDictionary.MergedDictionaries>
            <ui:BoolToVisibilityConverter x:Key="TrueToVis"/>
            <ui:BoolToVisibilityConverter x:Key="FalseToVis" Invert="True"/>

            <Style x:Key="FooterIcon" TargetType="TextBlock">
                <Setter Property="FontFamily" Value="Segoe MDL2 Assets"/>
                <Setter Property="FontSize" Value="16"/>
                <Setter Property="Foreground" Value="{StaticResource MutedBrush}"/>
                <Setter Property="VerticalAlignment" Value="Center"/>
            </Style>
        </ResourceDictionary>
    </Window.Resources>

    <Border Background="{StaticResource BgBrush}" CornerRadius="16"
            BorderBrush="{StaticResource LineBrush}" BorderThickness="1" Padding="24,18">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="*"/>
                <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>

            <Grid Grid.Row="0" Margin="0,0,0,12">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>
                <StackPanel Orientation="Horizontal">
                    <Border Width="40" Height="40" CornerRadius="10" Background="{StaticResource CardBrush}"
                            BorderBrush="{StaticResource LineBrush}" BorderThickness="1">
                        <TextBlock Text="&#xE943;" FontFamily="Segoe MDL2 Assets" FontSize="18"
                                   Foreground="{StaticResource MintBrush}"
                                   HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Border>
                    <StackPanel Margin="12,0,0,0" VerticalAlignment="Center">
                        <StackPanel Orientation="Horizontal">
                            <TextBlock Text="Codex" FontSize="24" FontWeight="SemiBold"
                                       Foreground="{StaticResource TextBrush}"/>
                            <Border Margin="10,2,0,0" Padding="8,1" CornerRadius="6"
                                    Background="{StaticResource CardBrush}"
                                    BorderBrush="{StaticResource LineBrush}" BorderThickness="1"
                                    Visibility="{Binding HasPlanBadge, Converter={StaticResource TrueToVis}}">
                                <TextBlock Text="{Binding PlanLabel}" FontSize="11"
                                           Foreground="{StaticResource MutedBrush}"/>
                            </Border>
                        </StackPanel>
                        <TextBlock Margin="0,2,0,0" FontSize="12" Foreground="{StaticResource MutedBrush}">
                            <Run Text="额度看板"/><Run Text="  /  "/><Run Text="{Binding TodayLabel}"/>
                        </TextBlock>
                    </StackPanel>
                </StackPanel>

                <StackPanel Grid.Column="1" Orientation="Horizontal" VerticalAlignment="Center">
                    <Ellipse Width="7" Height="7" VerticalAlignment="Center">
                        <Ellipse.Style>
                            <Style TargetType="Ellipse">
                                <Setter Property="Fill" Value="{StaticResource MutedBrush}"/>
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding SyncAccentKey}" Value="Mint">
                                        <Setter Property="Fill" Value="{StaticResource MintBrush}"/>
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </Ellipse.Style>
                    </Ellipse>
                    <TextBlock Margin="6,0,0,0" FontSize="12" Foreground="{StaticResource MutedBrush}"
                               Text="{Binding SyncLabel}" VerticalAlignment="Center"/>
                    <TextBlock Margin="18,0,0,0" FontSize="30" FontWeight="Light"
                               Foreground="{StaticResource TextBrush}" Text="{Binding Clock}"/>
                </StackPanel>
            </Grid>

            <ItemsControl Grid.Row="1" ItemsSource="{Binding Cards}">
                <ItemsControl.ItemsPanel>
                    <ItemsPanelTemplate>
                        <UniformGrid Rows="1" Columns="{Binding CardColumns}"/>
                    </ItemsPanelTemplate>
                </ItemsControl.ItemsPanel>
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <Border Margin="0,0,8,0" Background="{StaticResource CardBrush}" CornerRadius="12"
                                BorderBrush="{StaticResource LineBrush}" BorderThickness="1" Padding="18,14">
                            <Grid>
                                <Grid.RowDefinitions>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="*"/>
                                    <RowDefinition Height="Auto"/>
                                </Grid.RowDefinitions>

                                <Grid Grid.Row="0">
                                    <StackPanel Orientation="Horizontal">
                                        <TextBlock Text="{Binding Icon}" FontFamily="Segoe MDL2 Assets"
                                                   FontSize="14" VerticalAlignment="Center">
                                            <TextBlock.Style>
                                                <Style TargetType="TextBlock">
                                                    <Setter Property="Foreground" Value="{StaticResource MutedBrush}"/>
                                                    <Style.Triggers>
                                                        <DataTrigger Binding="{Binding AccentKey}" Value="Mint">
                                                            <Setter Property="Foreground" Value="{StaticResource MintBrush}"/>
                                                        </DataTrigger>
                                                        <DataTrigger Binding="{Binding AccentKey}" Value="Mist">
                                                            <Setter Property="Foreground" Value="{StaticResource MistBrush}"/>
                                                        </DataTrigger>
                                                        <DataTrigger Binding="{Binding AccentKey}" Value="Amber">
                                                            <Setter Property="Foreground" Value="{StaticResource AmberBrush}"/>
                                                        </DataTrigger>
                                                        <DataTrigger Binding="{Binding AccentKey}" Value="Coral">
                                                            <Setter Property="Foreground" Value="{StaticResource CoralBrush}"/>
                                                        </DataTrigger>
                                                    </Style.Triggers>
                                                </Style>
                                            </TextBlock.Style>
                                        </TextBlock>
                                        <TextBlock Margin="8,0,0,0" Text="{Binding Title}" FontSize="15"
                                                   Foreground="{StaticResource TextBrush}" VerticalAlignment="Center"/>
                                    </StackPanel>
                                    <TextBlock HorizontalAlignment="Right" VerticalAlignment="Center"
                                               Text="{Binding Code}" FontSize="10"
                                               Foreground="{StaticResource MutedBrush}"/>
                                </Grid>

                                <Grid Grid.Row="1" Margin="0,6,0,0">
                                    <StackPanel Orientation="Horizontal">
                                        <TextBlock Text="{Binding RemainingPercent}" FontSize="54" FontWeight="Light"
                                                   VerticalAlignment="Center">
                                            <TextBlock.Style>
                                                <Style TargetType="TextBlock">
                                                    <Setter Property="Foreground" Value="{StaticResource MintBrush}"/>
                                                    <Style.Triggers>
                                                        <DataTrigger Binding="{Binding AccentKey}" Value="Mist">
                                                            <Setter Property="Foreground" Value="{StaticResource MistBrush}"/>
                                                        </DataTrigger>
                                                        <DataTrigger Binding="{Binding AccentKey}" Value="Amber">
                                                            <Setter Property="Foreground" Value="{StaticResource AmberBrush}"/>
                                                        </DataTrigger>
                                                        <DataTrigger Binding="{Binding AccentKey}" Value="Coral">
                                                            <Setter Property="Foreground" Value="{StaticResource CoralBrush}"/>
                                                        </DataTrigger>
                                                    </Style.Triggers>
                                                </Style>
                                            </TextBlock.Style>
                                        </TextBlock>
                                        <TextBlock Text="%" FontSize="22" Margin="2,0,0,12"
                                                   VerticalAlignment="Bottom" Foreground="{StaticResource MutedBrush}"/>
                                    </StackPanel>
                                    <StackPanel HorizontalAlignment="Right" VerticalAlignment="Center">
                                        <TextBlock Text="剩余额度" FontSize="12" HorizontalAlignment="Right"
                                                   Foreground="{StaticResource TextBrush}"/>
                                        <TextBlock Text="{Binding UsedLabel}" FontSize="11" Margin="0,3,0,0"
                                                   HorizontalAlignment="Right" Foreground="{StaticResource MutedBrush}"/>
                                    </StackPanel>
                                </Grid>

                                <ItemsControl Grid.Row="2" ItemsSource="{Binding Bars}" Margin="0,8,0,4">
                                    <ItemsControl.ItemsPanel>
                                        <ItemsPanelTemplate>
                                            <UniformGrid Rows="1"/>
                                        </ItemsPanelTemplate>
                                    </ItemsControl.ItemsPanel>
                                    <ItemsControl.ItemTemplate>
                                        <DataTemplate>
                                            <Rectangle Height="20" RadiusX="2" RadiusY="2" Margin="0,0,3,0">
                                                <Rectangle.Style>
                                                    <Style TargetType="Rectangle">
                                                        <Setter Property="Fill" Value="{StaticResource DimBarBrush}"/>
                                                        <Style.Triggers>
                                                            <MultiDataTrigger>
                                                                <MultiDataTrigger.Conditions>
                                                                    <Condition Binding="{Binding On}" Value="True"/>
                                                                    <Condition Binding="{Binding AccentKey}" Value="Mint"/>
                                                                </MultiDataTrigger.Conditions>
                                                                <Setter Property="Fill" Value="{StaticResource MintBrush}"/>
                                                            </MultiDataTrigger>
                                                            <MultiDataTrigger>
                                                                <MultiDataTrigger.Conditions>
                                                                    <Condition Binding="{Binding On}" Value="True"/>
                                                                    <Condition Binding="{Binding AccentKey}" Value="Mist"/>
                                                                </MultiDataTrigger.Conditions>
                                                                <Setter Property="Fill" Value="{StaticResource MistBrush}"/>
                                                            </MultiDataTrigger>
                                                            <MultiDataTrigger>
                                                                <MultiDataTrigger.Conditions>
                                                                    <Condition Binding="{Binding On}" Value="True"/>
                                                                    <Condition Binding="{Binding AccentKey}" Value="Amber"/>
                                                                </MultiDataTrigger.Conditions>
                                                                <Setter Property="Fill" Value="{StaticResource AmberBrush}"/>
                                                            </MultiDataTrigger>
                                                            <MultiDataTrigger>
                                                                <MultiDataTrigger.Conditions>
                                                                    <Condition Binding="{Binding On}" Value="True"/>
                                                                    <Condition Binding="{Binding AccentKey}" Value="Coral"/>
                                                                </MultiDataTrigger.Conditions>
                                                                <Setter Property="Fill" Value="{StaticResource CoralBrush}"/>
                                                            </MultiDataTrigger>
                                                        </Style.Triggers>
                                                    </Style>
                                                </Rectangle.Style>
                                            </Rectangle>
                                        </DataTemplate>
                                    </ItemsControl.ItemTemplate>
                                </ItemsControl>

                                <Grid Grid.Row="3" Margin="0,0,0,6">
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="Auto"/>
                                        <ColumnDefinition Width="*"/>
                                        <ColumnDefinition Width="*"/>
                                        <ColumnDefinition Width="*"/>
                                        <ColumnDefinition Width="Auto"/>
                                    </Grid.ColumnDefinitions>
                                    <TextBlock Grid.Column="0" Text="0" FontSize="9" Foreground="{StaticResource MutedBrush}"/>
                                    <TextBlock Grid.Column="1" Text="25" FontSize="9" HorizontalAlignment="Center" Foreground="{StaticResource MutedBrush}"/>
                                    <TextBlock Grid.Column="2" Text="50" FontSize="9" HorizontalAlignment="Center" Foreground="{StaticResource MutedBrush}"/>
                                    <TextBlock Grid.Column="3" Text="75" FontSize="9" HorizontalAlignment="Center" Foreground="{StaticResource MutedBrush}"/>
                                    <TextBlock Grid.Column="4" Text="100%" FontSize="9" HorizontalAlignment="Right" Foreground="{StaticResource MutedBrush}"/>
                                </Grid>

                                <Separator Grid.Row="4" Margin="0,2,0,8" Background="{StaticResource LineBrush}"/>

                                <Grid Grid.Row="5">
                                    <StackPanel VerticalAlignment="Center">
                                        <TextBlock Text="距离重置" FontSize="11" Foreground="{StaticResource MutedBrush}"/>
                                        <StackPanel Orientation="Horizontal" Margin="0,3,0,0">
                                            <Ellipse Width="5" Height="5" VerticalAlignment="Center"
                                                     Fill="{StaticResource DimBarBrush}"/>
                                            <TextBlock Margin="6,0,0,0" Text="{Binding ResetsAtText}" FontSize="11"
                                                       Foreground="{StaticResource MutedBrush}" VerticalAlignment="Center"/>
                                        </StackPanel>
                                    </StackPanel>
                                    <TextBlock Text="{Binding Countdown}" HorizontalAlignment="Right"
                                               VerticalAlignment="Center" FontSize="20" FontWeight="SemiBold"
                                               Foreground="{StaticResource TextBrush}"/>
                                </Grid>
                            </Grid>
                        </Border>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>

            <StackPanel Grid.Row="0" Grid.RowSpan="3" VerticalAlignment="Center" HorizontalAlignment="Center"
                        Visibility="{Binding HasData, Converter={StaticResource FalseToVis}}">
                <TextBlock Text="未检测到 Codex 用量数据" FontSize="18" HorizontalAlignment="Center"
                           Foreground="{StaticResource TextBrush}"/>
                <TextBlock Text="{Binding EmptyHint}" FontSize="11" Margin="0,10,0,0" TextAlignment="Center"
                           Foreground="{StaticResource MutedBrush}"/>
            </StackPanel>

            <Grid Grid.Row="2" Margin="0,12,0,0">
                <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
                    <TextBlock Text="额外点数" FontSize="11" Foreground="{StaticResource MutedBrush}"/>
                    <TextBlock Text="{Binding CreditsLabel}" FontSize="11" Margin="6,0,0,0"
                               Foreground="{StaticResource TextBrush}"/>
                    <TextBlock Text="|" FontSize="11" Margin="10,0,10,0" Foreground="{StaticResource LineBrush}"/>
                    <TextBlock Text="{Binding UpdatedLabel}" FontSize="11" Foreground="{StaticResource MutedBrush}"/>
                </StackPanel>
                <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                    <Button x:Name="RefreshButton" Width="28" Height="28" Background="Transparent"
                            BorderThickness="0" Cursor="Hand" ToolTip="立即刷新" Click="OnRefreshClick">
                        <TextBlock Text="&#xE72C;" Style="{StaticResource FooterIcon}"/>
                    </Button>
                    <Button x:Name="SettingsButton" Width="28" Height="28" Background="Transparent"
                            BorderThickness="0" Cursor="Hand" ToolTip="打开配置目录" Click="OnSettingsClick">
                        <TextBlock Text="&#xE713;" Style="{StaticResource FooterIcon}"/>
                    </Button>
                </StackPanel>
            </Grid>
        </Grid>
    </Border>
</Window>
```

- [ ] **Step 8: 写 `Ui/PanelWindow.xaml.cs`**

```csharp
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CodexUsagePanel.Input;

namespace CodexUsagePanel.Ui;

public partial class PanelWindow : Window
{
    private readonly DispatcherTimer _tick;
    private readonly Action _onRefresh;
    private readonly Action _onOpenSettings;
    private readonly Action<DateTime> _onTimeTick;
    private int _autoHideSeconds;
    private DateTime _shownAt;

    public PanelWindow(Action onRefresh, Action onOpenSettings, Action<DateTime> onTimeTick)
    {
        InitializeComponent();
        _onRefresh = onRefresh;
        _onOpenSettings = onOpenSettings;
        _onTimeTick = onTimeTick;

        // 只在面板可见时跑，避免常驻空转（隐藏时由 HidePanel 显式 Stop）
        _tick = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += OnTick;
    }

    public bool IsPanelVisible => IsVisible;

    public (int Left, int Top, int Right, int Bottom)? CurrentBoundsPhysical
    {
        get
        {
            if (!IsVisible) return null;

            var dpi = VisualTreeHelper.GetDpi(this);
            double physicalPerDip = dpi.DpiScaleX;

            return (
                (int)(Left * physicalPerDip),
                (int)(Top * physicalPerDip),
                (int)((Left + ActualWidth) * physicalPerDip),
                (int)((Top + ActualHeight) * physicalPerDip));
        }
    }

    public void ShowPanel(PanelView view, int autoHideSeconds)
    {
        DataContext = view;
        _autoHideSeconds = autoHideSeconds;

        var dpi = VisualTreeHelper.GetDpi(this);
        var cursor = NativeInterop.GetCursorPosition();
        var work = NativeInterop.GetWorkAreaNear(cursor.X, cursor.Y);
        var (left, top) = PanelPositioner.Resolve(
            cursor.X, cursor.Y,
            work.Left, work.Top, work.Right, work.Bottom,
            dpi.DpiScaleX, Width, Height);

        Left = left;
        Top = top;

        Show();
        _shownAt = DateTime.UtcNow;
        _tick.Start();
    }

    public void Refresh(PanelView view) => DataContext = view;

    public void HidePanel()
    {
        _tick.Stop();
        Hide();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeInterop.GetWindowLongPtr(handle, NativeInterop.GWL_EXSTYLE);
        NativeInterop.SetWindowLongPtr(
            handle,
            NativeInterop.GWL_EXSTYLE,
            (IntPtr)(style.ToInt64() | NativeInterop.WS_EX_TOOLWINDOW | NativeInterop.WS_EX_NOACTIVATE));
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // 只把新的时间交给视图做属性级更新；不重设 DataContext
        _onTimeTick(DateTime.Now);

        if ((DateTime.UtcNow - _shownAt).TotalSeconds >= _autoHideSeconds) HidePanel();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _onRefresh();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => _onOpenSettings();
}
```

- [ ] **Step 9: 编译 + 全部测试**

```bash
cd "E:/Viding/tip page" && dotnet build CodexUsagePanel.sln --nologo && dotnet test CodexUsagePanel.sln --nologo
```

Expected: 构建成功；`Passed! - Failed: 0`

- [ ] **Step 10: 提交**

```bash
set -a && . "E:/Viding/FDE-self-study/.qoder/env" && set +a && \
git -C "E:/Viding/tip page" add src/CodexUsagePanel/Ui tests && \
git -C "E:/Viding/tip page" commit -m "feat: 无边框置顶面板、主题与多屏 DPI 定位"
```

---

### Task 10: 托盘、开机自启、单实例与组装

**Files:**
- Create: `src/CodexUsagePanel/Tray/AutoStart.cs`、`src/CodexUsagePanel/Tray/TrayManager.cs`
- Modify: `src/CodexUsagePanel/App.xaml.cs`
- Create: `tests/CodexUsagePanel.Tests/Tray/AutoStartTests.cs`

**Interfaces:**
- Consumes: `CodexUsageReader`、`AppSettings`、`DoubleCtrlHook`、`ClickAwayHook`、`PanelWindow`、`PanelView`
- Produces: `AutoStart.BuildCommand(string exePath) -> string`、`AutoStart.IsEnabled()`、`AutoStart.Set(bool enabled, string exePath)`；`TrayManager`（构造入参 `Action showPanel`、`Action exit`、`Action togglePause`、`Func<bool> isPaused`、`Func<bool> autoStartEnabled`，方法 `UpdateIcon(UsageSnapshot?)`）。组装完成的进程常驻物只有：托盘图标 + 两个低级钩子 + 一个隐藏窗口，**没有定时器**。

- [ ] **Step 1: 写失败的测试**

```csharp
using CodexUsagePanel.Tray;
using Xunit;

namespace CodexUsagePanel.Tests.Tray;

public class AutoStartTests
{
    [Fact]
    public void BuildCommandQuotesPathContainingSpaces()
    {
        var command = AutoStart.BuildCommand(@"E:\Viding\tip page\publish\CodexUsagePanel.exe");
        Assert.Equal("\"E:\\Viding\\tip page\\publish\\CodexUsagePanel.exe\" --tray", command);
    }

    [Fact]
    public void BuildCommandKeepsShortPathIntact()
    {
        var command = AutoStart.BuildCommand(@"C:\apps\cup.exe");
        Assert.Equal("\"C:\\apps\\cup.exe\" --tray", command);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
cd "E:/Viding/tip page" && dotnet test CodexUsagePanel.sln --nologo --filter "FullyQualifiedName~AutoStartTests"
```

Expected: 编译错误 `The name 'AutoStart' does not exist`

- [ ] **Step 3: 实现 `Tray/AutoStart.cs`**

```csharp
using Microsoft.Win32;

namespace CodexUsagePanel.Tray;

public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexUsagePanel";

    public static string BuildCommand(string exePath) => $"\"{exePath}\" --tray";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value && value.Length > 0;
    }

    public static bool Set(bool enabled, string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

            if (key is null) return false;

            if (enabled) key.SetValue(ValueName, BuildCommand(exePath));
            else key.DeleteValue(ValueName, throwOnMissingValue: false);

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

```bash
cd "E:/Viding/tip page" && dotnet test CodexUsagePanel.sln --nologo --filter "FullyQualifiedName~AutoStartTests"
```

Expected: `Passed! - Failed: 0, Passed: 2`

- [ ] **Step 5: 实现 `Tray/TrayManager.cs`**

```csharp
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using CodexUsagePanel.Data;
using Hardcodet.Wpf.TaskbarNotification;

namespace CodexUsagePanel.Tray;

public sealed class TrayManager : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly TaskbarIcon _icon;
    private readonly MenuItem _autoStartItem;
    private readonly MenuItem _pauseItem;
    private readonly Action _onTogglePause;
    private int _lastIconUsed;

    public TrayManager(Action showPanel, Action exit, Action togglePause, Func<bool> isPaused, Func<bool> autoStartEnabled)
    {
        _onTogglePause = togglePause;

        var menu = new ContextMenu();

        var show = new MenuItem { Header = "显示面板" };
        show.Click += (_, _) => showPanel();

        _autoStartItem = new MenuItem { Header = "开机自启", IsCheckable = true, IsChecked = autoStartEnabled() };
        _autoStartItem.Click += (_, _) =>
        {
            bool requested = _autoStartItem.IsChecked;
            if (!AutoStart.Set(requested, Environment.ProcessPath ?? string.Empty))
            {
                _autoStartItem.IsChecked = !requested;
                _icon.ShowBalloonTip("开机自启", "写入注册表失败，可能被安全软件拦截。", BalloonIcon.Warning);
            }
        };

        _pauseItem = new MenuItem { Header = "暂停全局热键", IsCheckable = true, IsChecked = isPaused() };

        var quit = new MenuItem { Header = "退出" };
        quit.Click += (_, _) => exit();

        menu.Items.Add(show);
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);

        _icon = new TaskbarIcon
        {
            ToolTipText = "Codex 额度看板（双击 Ctrl 唤出）",
            ContextMenu = menu,
            Icon = BuildIcon(0)
        };

        _icon.TrayMouseDoubleClick += (_, _) => showPanel();
        _pauseItem.Click += (_, _) => _onTogglePause();
    }

    public void UpdateIcon(UsageSnapshot? snapshot)
    {
        int used = snapshot?.Primary?.UsedPercentRounded ?? 0;

        // 百分比没变就不重画：避免每次唤出都新建 Bitmap + HICON 并触发 Shell 重绘
        if (used == _lastIconUsed) return;
        _lastIconUsed = used;

        var previous = _icon.Icon;
        _icon.Icon = BuildIcon(used);
        previous?.Dispose();
    }

    public void ShowHotkeyWarning() =>
        _icon.ShowBalloonTip("全局热键不可用", "键盘钩子注册失败，请从托盘菜单打开面板。", BalloonIcon.Warning);

    public void Dispose() => _icon.Dispose();

    private static Icon BuildIcon(int usedPercent)
    {
        using var bitmap = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            graphics.Clear(Color.FromArgb(14, 20, 22));

            int filled = (int)Math.Round(Math.Clamp(usedPercent, 0, 100) / 100.0 * 12);
            using var brush = new SolidBrush(usedPercent >= 100
                ? Color.FromArgb(239, 107, 107)
                : usedPercent >= 80
                    ? Color.FromArgb(240, 168, 96)
                    : Color.FromArgb(74, 222, 180));

            if (filled > 0) graphics.FillRectangle(brush, 4, 2 + (12 - filled), 8, filled);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
```

- [ ] **Step 6: 组装 `App.xaml.cs`**

```csharp
using System.IO;
using System.Windows;
using CodexUsagePanel.Data;
using CodexUsagePanel.Input;
using CodexUsagePanel.Settings;
using CodexUsagePanel.Tray;
using CodexUsagePanel.Ui;

namespace CodexUsagePanel;

public partial class App : Application
{
    private static Mutex? _singleInstance;

    private AppSettings _settings = new();
    private CodexUsageReader _reader = null!;
    private DoubleCtrlHook _keyboardHook = null!;
    private ClickAwayHook _mouseHook = null!;
    private PanelWindow _panel = null!;
    private TrayManager _tray = null!;
    private PanelView? _currentView;
    private bool _hotkeyPaused;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\CodexUsagePanel.SingleInstance", out bool created);
        if (!created)
        {
            Shutdown();
            return;
        }

        _settings = AppSettings.Load(AppSettings.DefaultPath());

        var sessionsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "sessions");

        _reader = new CodexUsageReader(sessionsRoot, TimeSpan.FromSeconds(_settings.RefreshThrottleSeconds));
        _panel = new PanelWindow(onRefresh: RefreshNow, onOpenSettings: OpenSettingsFolder, onTimeTick: OnTimeTick);

        // 两个钩子都在 UI 线程构造，内部会用 SynchronizationContext.Current 把动作 Post 回来
        _mouseHook = new ClickAwayHook(() => _panel.CurrentBoundsPhysical, onClickAway: HidePanel);
        _keyboardHook = new DoubleCtrlHook(
            _settings.DoubleCtrlWindowMs,
            isPanelVisible: () => _panel.IsPanelVisible,
            onToggle: TogglePanel,
            onEscape: HidePanel);

        _tray = new TrayManager(
            showPanel: ShowPanel,
            exit: ExitApplication,
            togglePause: TogglePause,
            isPaused: () => _hotkeyPaused,
            autoStartEnabled: AutoStart.IsEnabled);

        if (!_keyboardHook.Start())
        {
            _tray.ShowHotkeyWarning();
        }

        // 启动时读一次，让托盘图标一开始就反映当前用量；之后不再有任何后台定时器
        _tray.UpdateIcon(_reader.Read());
    }

    private void TogglePanel()
    {
        if (_panel.IsPanelVisible) HidePanel();
        else ShowPanel();
    }

    private void ShowPanel()
    {
        var snapshot = _reader.Read();   // 15 秒节流，命中缓存时不碰磁盘
        var view = PanelView.Build(snapshot, DateTime.Now, _settings.PlanLabel);
        _currentView = view;
        _panel.ShowPanel(view, _settings.AutoHideSeconds);
        _mouseHook.Start();
        _tray.UpdateIcon(snapshot);
    }

    private void HidePanel()
    {
        _panel.HidePanel();
        _mouseHook.Stop();
    }

    private void OnTimeTick(DateTime now) => _currentView?.Tick(now);

    private void RefreshNow()
    {
        _reader.InvalidateCache();
        var snapshot = _reader.Read();
        var view = PanelView.Build(snapshot, DateTime.Now, _settings.PlanLabel);
        _currentView = view;

        if (_panel.IsPanelVisible) _panel.Refresh(view);
        _tray.UpdateIcon(snapshot);
    }

    private void TogglePause()
    {
        _hotkeyPaused = !_hotkeyPaused;

        if (_hotkeyPaused) _keyboardHook.Stop();
        else _keyboardHook.Start();
    }

    private void OpenSettingsFolder()
    {
        var path = AppSettings.DefaultPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) _settings.Save(path);
        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    private void ExitApplication()
    {
        _mouseHook.Dispose();
        _keyboardHook.Dispose();
        _tray.Dispose();
        _singleInstance?.ReleaseMutex();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mouseHook?.Dispose();
        _keyboardHook?.Dispose();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
```

- [ ] **Step 7: 在 `CodexUsagePanel.csproj` 中补 `ApplicationDefinition` 检查并编译**

`dotnet new wpf` 已自动包含 `App.xaml` 与 `app.manifest` 的配置，无需手工添加。执行：

```bash
cd "E:/Viding/tip page" && dotnet build CodexUsagePanel.sln --nologo
```

Expected: 构建成功，0 error

- [ ] **Step 8: 手动冒烟**

```bash
cd "E:/Viding/tip page" && dotnet run --project src/CodexUsagePanel
```

Expected: 托盘出现图标；双击 Ctrl 在光标处弹出面板并显示真实额度；Esc 关闭；托盘右键四项菜单齐全。确认后 `Ctrl+C` 结束。

- [ ] **Step 9: 提交**

```bash
set -a && . "E:/Viding/FDE-self-study/.qoder/env" && set +a && \
git -C "E:/Viding/tip page" add src tests && \
git -C "E:/Viding/tip page" commit -m "feat: 托盘常驻、开机自启、单实例与全局装配"
```

---

### Task 11: 发布与验收

**Files:**
- Create: `README.md`
- Modify: 无

**Interfaces:**
- Consumes: 全部
- Produces: `publish/` 下的可执行文件

- [ ] **Step 1: 写 `README.md`**

```markdown
# Codex 额度悬浮看板

双击 Ctrl 唤出悬浮面板，显示 Codex 的 5 小时额度、本周额度、重置倒计时与额外点数。
数据全部来自本机 `~/.codex/sessions` 下 Codex 自己写的额度快照，**不联网、不读取登录凭证**。

## 运行

双击 `CodexUsagePanel.exe`，程序常驻托盘。

- 双击 Ctrl：弹出 / 收起面板
- Esc 或点击面板外：关闭
- 托盘右键：显示面板 / 开机自启 / 暂停全局热键 / 退出

## 配置

`%APPDATA%\CodexUsagePanel\settings.json`：

| 字段 | 默认 | 说明 |
|---|---|---|
| `DoubleCtrlWindowMs` | 400 | 两次 Ctrl 的最大间隔（150–1000） |
| `AutoHideSeconds` | 10 | 无操作自动隐藏（1–120） |
| `RefreshThrottleSeconds` | 15 | 重新扫描 session 文件的最小间隔（5–300） |
| `PlanLabel` | `PLUS` | 标题右侧角标文字，留空则不显示 |
| `FixedPositionEnabled` | false | 是否固定在指定坐标而非跟随光标 |
| `FixedPositionX` / `FixedPositionY` | 0 / 0 | 固定坐标（DIP） |

## 已知限制

- 额度是 Codex 上次请求时写入的快照，长时间不用 Codex 会滞后，界面会显示"快照滞后"。
- 快速连按 Ctrl（例如连打 Ctrl+C）可能误触发，托盘菜单可临时暂停热键。

## 资源占用

常驻代价按"接近零"设计：

- 面板隐藏时没有任何定时器、后台线程或轮询循环，CPU 占用为 0；
- 只有双击 Ctrl、点托盘、点刷新按钮时才读一次 session 文件尾部（通常 256 KB）；
- 面板可见期间每秒只刷新三段倒计时文本，不重建界面、不读文件；
- 托盘图标只在百分比变化时重画。

## 构建

```bash
dotnet publish src/CodexUsagePanel -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true
```
```

- [ ] **Step 2: 发布依赖运行时的版本**

```bash
cd "E:/Viding/tip page" && \
dotnet publish src/CodexUsagePanel -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o publish
```

Expected: `publish/CodexUsagePanel.exe` 存在

`PublishReadyToRun=true` 把 IL 预编译成原生代码，减少启动时的 JIT 线程与 CPU 峰值；代价是 publish 体积变大，内存占用基本不变。框架依赖发布依赖机器上已装的 .NET 8 Desktop Runtime（开发机装 SDK 时已包含）。

- [ ] **Step 3: 按 spec §8.2 逐条手动验收**

- [ ] 双击 Ctrl 在光标处弹出面板
- [ ] 长按 Ctrl 3 秒不触发
- [ ] 面板弹出后原窗口输入光标未失焦，可继续打字
- [ ] Alt-Tab 列表中不出现本工具
- [ ] Esc 关闭；面板外点击关闭；10 秒自动隐藏
- [ ] 再次双击 Ctrl 关闭（切换语义）
- [ ] 数值与 Codex CLI 底部状态栏显示的百分比一致
- [ ] 快照超过 120 秒时右上角变灰点"快照滞后"
- [ ] 主显示器 + 副显示器各弹一次，125%/150% 缩放下位置不越界
- [ ] 托盘四项菜单均可用；"暂停全局热键"后双击 Ctrl 无反应
- [ ] 开机自启勾选后重启仍在；取消勾选后注册表项消失
- [ ] 重命名 `~/.codex/sessions` 后面板显示无数据文案而非崩溃，改回后恢复正常
- [ ] 面板弹出到出数据 < 150 ms（秒表或肉眼无等待感）

- [ ] **Step 4: 资源占用验收（本工具的第一优先级指标）**

先启动发布版、把面板收起，静置 60 秒让工作集稳定，然后连续采样两次（间隔 60 秒）：

```bash
powershell -NoProfile -Command "$p = Get-Process CodexUsagePanel; \
$ws = $p.WorkingSet64/1MB; $cpu = $p.CPU; \
'第 1 次: {0:N1} MB 工作集, CPU {1:N3}s' -f $ws, $cpu; \
Start-Sleep -Seconds 60; \
$p.Refresh(); \
'第 2 次: {0:N1} MB 工作集, CPU {1:N3}s' -f ($p.WorkingSet64/1MB), $p.CPU"
```

判定标准：

| 指标 | 预算 |
|---|---|
| 60 秒内 CPU 增量 | < 0.06 s（平均 < 0.1%） |
| 工作集（面板隐藏） | ≤ 80 MB |
| 面板可见时 CPU | < 1%（任务管理器观察即可） |

再连续唤出 / 收起 20 次，然后每 20 秒采一次句柄数，共 3 次：

```bash
powershell -NoProfile -Command "1..3 | ForEach-Object { (Get-Process CodexUsagePanel).HandleCount; Start-Sleep -Seconds 20 }"
```

三次结果波动应 < 50 且无单调上升（单调上升说明 `Icon` / `HICON` 或窗口句柄泄漏）。若工作集超过 80 MB，先检查是否误留了周期定时器（隐藏状态下 CPU 应为 0）以及是否每次刷新都重设了 `DataContext`。

- [ ] **Step 5: 提交**

```bash
set -a && . "E:/Viding/FDE-self-study/.qoder/env" && set +a && \
git -C "E:/Viding/tip page" add README.md && \
git -C "E:/Viding/tip page" commit -m "docs: 使用说明与已知限制"
```

- [ ] **Step 6: 向用户报告完成，询问是否推送到远端**

**不要自行 `git push`。** 询问用户：

> 全部实现完成并通过验收。是否推送到 `https://github.com/Hzsurpassday/tip-page`？

---

## 与 spec 的偏差（已与用户确认）

1. **§3.2 PLUS 角标**：实测 `plan_type` 恒为 `null`（8 MB 尾部数据里一条非空的都没有），`config.toml` 也无套餐信息。改为 `settings.json` 的 `PlanLabel`，用户填一次。
2. **§3.3 隐私**：session 文件含用户完整对话正文（实测单文件 54 处 `input_text`）。保留"只读尾部 + 只取 `payload.rate_limits`"的设计，并追加硬约束：异常/日志不得含行内容。
3. **§9 依赖**：新增 `System.Drawing.Common`（托盘图标位图）。总依赖数 2。
4. **§8.1 测试 fixture**：改为运行时在临时目录生成，不把 4 MB 级别 fixture 提交进仓库。
5. **新增单实例互斥**：`Local\CodexUsagePanel.SingleInstance`，避免开机自启叠加手动启动时挂两个键盘钩子。
6. **§6 额外点数**：`credits.balance` 可能为 `null`（实测），此时显示 `0`。
7. **§4.2 / §4.3 钩子改为事件驱动（资源优先）**：原设计是"钩子置标志位 + UI 轮询"，计划初稿用的是 80 ms `DispatcherTimer`（每秒唤醒 12.5 次，常驻不划算）。改为钩子在回调里 `SynchronizationContext.Post` 回 UI 线程，彻底去掉轮询定时器。spec §4.0/§4.2/§4.3 已同步更新。
8. **§4.4 视图模型改为增量通知**：`PanelView` / `CardView` 从不可变 record 改成实现 `INotifyPropertyChanged` 的类，1 秒心跳只更新时钟、倒计时、更新龄三段文本，不重设 `DataContext`（否则每秒重建整个可视树）。
9. **倒计时向上取整**：原 `FormatCountdown` 按截断分钟计算，面板弹出 1 秒后计数就会跳掉一整分钟。改为向上取整到分钟，并加测试锁住。
10. **§9 构建 + 内存策略**：publish 追加 `-p:PublishReadyToRun=true`；csproj 设 `ConcurrentGarbageCollection=false` / `ServerGarbageCollection=false`，去掉后台 GC 线程。
11. **§8.2 验收指标**：内存门槛从"< 60 MB"改为更可验证的"工作集 ≤ 80 MB + 空闲 60 秒 CPU 增量 < 0.06 s"，并新增句柄泄漏检查。
