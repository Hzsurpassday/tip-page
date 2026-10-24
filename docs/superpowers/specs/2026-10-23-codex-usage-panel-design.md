# Codex 额度悬浮看板 · 设计说明

日期：2026-10-23
项目根目录：`E:\Viding\tip page`
状态：待用户复核

## 1. 目标

一个 Windows 常驻小工具：双击 Ctrl 就地弹出悬浮面板，显示 Codex 的 5 小时额度、本周额度、
距离重置倒计时、额外点数；Esc 或点击面板外区域消失。数据来源是 Codex CLI 自己写在本地
session 文件里的额度快照，**不联网、不读取任何登录凭证**。

参考视觉：用户提供的截图（深色卡片式"额度看板"，Codex + PLUS 角标，两张卡各带竖条进度条）。

## 2. 明确不做（第一版范围外）

- 多平台聚合（Claude Code、Qoder、DeepSeek 等）——架构预留扩展点，但不实现
- 调用 ChatGPT 后端接口拉实时额度、读取 `~/.codex/auth.json`
- 主题切换、历史用量曲线、图表
- 设置界面（配置项通过 JSON 文件修改）
- 自定义热键（固定为双击 Ctrl，提供"暂停全局热键"开关兜底）

## 3. 数据来源与已验证事实

### 3.1 数据位置

`C:\Users\surpassday\.codex\sessions\YYYY\MM\DD\rollout-*.jsonl`

本机现状（2026-10-23 实测）：`sessions/2026/10/` 下有 11/15/16/17/18 五个日期目录，
共 65 个 `.jsonl` 文件；其中单个文件最大 **143,506,714 字节（约 137 MB）**。

### 3.2 数据格式（从真实文件抓取）

```json
"rate_limits":{
  "limit_id":"codex","limit_name":null,
  "primary":{"used_percent":98.0,"window_minutes":300,"resets_at":1792352158},
  "secondary":{"used_percent":63.0,"window_minutes":10080,"resets_at":1792881761},
  "credits":{"has_credits":false,"unlimited":false,"balance":"0"},
  "individual_limit":null,"spend_control_reached":null,
  "plan_type":null,"rate_limit_reached_type":null
}
```

字段映射到界面：

| 界面元素 | 来源字段 |
|---|---|
| 5 小时额度卡 · 百分比 | `primary.used_percent`（`window_minutes == 300`） |
| 5 小时额度卡 · 距离重置 | `primary.resets_at`（Unix 秒）－ 当前本地时间 |
| 5 小时额度卡 · 重置时间点 | `primary.resets_at` 转本地时区，格式 `2026-10-19 周一 18:25` |
| 本周额度卡 | `secondary.*`（`window_minutes == 10080`） |
| 额外点数 | `credits.balance`；`has_credits == false` 时显示 `0` |
| 已用 N% | `100 - used_percent`，四舍五入取整 |
| PLUS 角标 | `plan_type`；为 `null` 时回退读 `~/.codex/config.toml`（纯文本配置，不含凭证），仍无则不显示角标 |
| 已同步 / N 秒前更新 | 快照文件的 `LastWriteTime` 与当前时间差 |
| 右上角时钟 | 系统当前时间，`HH:mm`，每秒刷新（DispatcherTimer，仅面板可见时运行） |

### 3.3 读取策略（硬约束）

1. 枚举 `sessions/**/rollout-*.jsonl`，按 `LastWriteTime` 降序取最新文件。
2. **禁止整读文件**。以 `FileShare.ReadWrite` 打开，`Seek` 到末尾前 256 KB，逐行扫描，
   取**最后一次**出现的 `"rate_limits":{...}` 并解析。
3. 尾部 256 KB 内未命中 → 扩大到末尾 4 MB 重试一次。仍未命中 → 该文件视为无数据，
   继续尝试列表中下一个文件（最多尝试 3 个文件）。
4. 结果按 `(文件路径, LastWriteTime, 长度)` 缓存。缓存命中直接返回，不重读磁盘。
5. 定时刷新节流：距上次扫描不足 15 秒时直接返回缓存。

### 3.4 多条 rate_limits 的选择

同一文件里会出现 `limit_id` 为 `codex`、`premium` 等多种记录（`premium` 那条
primary/secondary 全为 `null`）。规则：**从后往前**取第一条 `primary != null || secondary != null`
的记录；不区分 `limit_id`，因为"最后写入且带窗口数据"的那条就是当前生效额度。

## 4. 组件划分

```
Program/App ──┬── Input/DoubleCtrlHook ──┐
              │                          ├──> Ui/PanelWindow ──> Data/CodexUsageReader ──> 磁盘
              ├── Input/ClickAwayHook ───┘
              ├── Tray/TrayManager
              └── Settings/AppSettings
```

每个单元的职责、用法、依赖：

### 4.0 资源占用（第一优先级约束）

这是常驻托盘工具，用户明确要求"最小程度占用内存或 CPU"，因此架构按以下规则取舍：

- **空闲即零**：面板隐藏时不得存在任何定时器、后台线程或轮询循环。常驻的只有两个低级钩子和一个托盘图标，CPU 占用为 0。
- **按需读盘**：只有用户双击 Ctrl、点托盘、点刷新按钮时才读一次 session 文件尾部（通常 256 KB，最多 4 MB）。没有周期性后台刷新。
- **心跳只改文本**：面板可见期间用 1 秒定时器刷新时钟、倒计时、"N 秒前更新"；只发属性变更通知，不重设 `DataContext`、不重建可视树、不读文件。面板隐藏时立即停止。
- **缓冲复用**：尾读缓冲走 `ArrayPool<byte>`，避免 4 MB 级数组进 LOH（大对象堆）。
- **图标去抖**：托盘图标只在百分比变化时重画（每次重画会新建 `Bitmap` + `HICON`）。
- **钩子不轮询**：低级钩子回调运行在安装它的 UI 线程上，命中后直接 `SynchronizationContext.Post` 一个动作回来，下一个消息即处理——比轮询快得多且空闲时零唤醒。
- **预算**：空闲 CPU 平均 < 0.1%，工作集 ≤ 80 MB（框架依赖发布）；面板可见 CPU < 1%。

### 4.1 `Data/CodexUsageReader.cs` + `Data/UsageSnapshot.cs`

- **做什么**：把 3.3 的读取策略实现为 `UsageSnapshot? Read()`。`UsageSnapshot` 是不可变记录，
  含 5 小时/本周两个 `UsageWindow(UsedPercent, WindowMinutes, ResetsAt)`、`CreditsBalance`、
  `PlanType`、`SourceFile`、`FileWrittenAt`。
- **怎么用**：`reader.Read()` → 快照或 `null`。构造时注入 sessions 根目录路径（测试用 fixture 目录）。
- **依赖**：仅 `System.IO`、`System.Text.Json`、`System.Text.RegularExpressions`。不依赖 WPF。

### 4.2 `Input/DoubleCtrlHook.cs`

- **做什么**：`SetWindowsHookEx(WH_KEYBOARD_LL)` 全局键盘钩子。Ctrl 按下后 400 ms 内出现
  第二次**独立**按下 → 触发"切换面板"；Esc 且面板可见 → 触发"关闭面板"。
- **怎么用**：`hook.Start()` / `hook.Stop()`，构造时传入 `onToggle` / `onEscape` 两个回调。
  命中后钩子内部用 `SynchronizationContext.Post` 把回调送回 UI 线程（见 4.0），调用方不需要轮询。
- **依赖**：Win32 P/Invoke。
- **两条必须遵守的规则**：
  - 过滤自动重复：`KBDLLHOOKSTRUCT.flags` 的 `LLKHF_UP` 位区分按下/抬起，并用
    `GetKeyState(VK_CONTROL) & 0x8000` 判断"前一次是否仍按住"，长按 Ctrl 只算一次按下。
  - 回调内**只做判断和 `Post`，然后立即 `CallNextHookEx`**。低级钩子超时预算约 300 ms，超时 Windows
    会静默卸载钩子导致功能失效，因此绝不在回调中触碰 UI 或磁盘。

### 4.3 `Input/ClickAwayHook.cs`

- **做什么**：低级鼠标钩子，仅在面板可见期间挂载。检测按下事件，坐标落在面板矩形外 →
  触发"关闭面板"（同样通过 `Post` 回 UI 线程）。
- **为什么需要**：见 5.1，面板不抢焦点，WPF 的 `Deactivated` 不会触发。

### 4.4 `Ui/PanelWindow.xaml(.cs)` + `Ui/PanelViewModel.cs` + `Ui/Theme.xaml`

- **做什么**：渲染快照，暴露 `ShowNear(Point)` / `Hide()`。ViewModel 只做快照到显示字符串的转换
  （倒计时格式化、颜色档位、"N 秒前更新"），不含 IO。时钟、倒计时、更新龄这三个随时间变化的属性
  实现 `INotifyPropertyChanged`，1 秒心跳只触发它们的变更通知；静态内容（标题、段数、配色）
  一次绑定后不再改动，不重设 `DataContext`。
- **依赖**：`UsageSnapshot`、两个钩子的回调。

### 4.5 `Tray/TrayManager.cs`

- **做什么**：托盘图标 + 右键菜单：`显示面板` / `开机自启`（勾选态）/ `暂停全局热键` / `退出`。
  左键双击托盘 = 弹面板。
- **依赖**：`Hardcodet.NotifyIcon.Wpf`（NuGet）。

### 4.6 `Settings/AppSettings.cs`

- **做什么**：读写 `%APPDATA%\CodexUsagePanel\settings.json`。字段：
  `DoubleCtrlWindowMs`(400)、`AutoHideSeconds`(10)、`FixedPosition`{`Enabled`,`X`,`Y`}、
  `RefreshThrottleSeconds`(15)。文件缺失或损坏时用默认值并重建，不抛异常给上层。

## 5. 窗口行为

### 5.1 不抢焦点

WPF 属性：`WindowStyle=None`、`AllowsTransparency=True`、`ResizeMode=NoResize`、
`Topmost=True`、`ShowInTaskbar=False`。`SourceInitialized` 时通过 `GetWindowLong/SetWindowLong`
追加 `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`。

- 收益：弹出时你原来窗口的焦点和输入光标不受影响，Alt-Tab 列表里不出现本工具。
- 代价（已在 4.3 处理）：拿不到 `Activated/Deactivated`，所以点击别处关闭必须靠鼠标钩子。

### 5.2 位置与尺寸

尺寸固定 720×420（逻辑像素）。位置：取光标所在显示器（`Screen.FromPoint`），弹在光标右下
偏移 (16, 16)，与 `SystemParameters.WorkArea` 求交后夹回边界，避免溢出屏幕。
`FixedPosition.Enabled = true` 时改用配置坐标：`FixedPositionX/Y` 是**虚拟屏幕物理像素**，
即窗口左上角的落点；该点所在显示器的 DPI 只影响窗口的 DIP↔物理换算，不影响落点。

坐标换算约定：Win32 钩子与 `Screen` 给的是**物理像素**，WPF 的 `Left/Top/Width/Height` 是
**DIP**。统一在弹出时读 `VisualTreeHelper.GetDpi(window)` 做一次物理→DIP 除法换算，
因此 125%/150% 缩放和多屏不同 DPI 下位置一致。验收时需在主显示器和副显示器各测一次。

### 5.3 消失条件（任一即隐藏）

- Esc（键盘钩子）
- 面板矩形外点击（鼠标钩子）
- 显示后 `AutoHideSeconds` 秒无操作（DispatcherTimer）
- 再次双击 Ctrl（切换语义）

隐藏时同时卸载鼠标钩子，避免空转。

## 6. 界面

复刻截图布局，深色底 `#0E1416`：

```
┌────────────────────────────────────────────────────────────┐
│ </>  Codex  [PLUS]              ● 已同步          17:17     │
│    额度看板 / 10.19 周一                                     │
│ ┌──────────────────────────┐ ┌──────────────────────────┐  │
│ │ 🕐 5 小时额度   5H WINDOW │ │ 📅 本周额度   7D WINDOW  │  │
│ │   49%        剩余额度    │ │   68%       剩余额度     │  │
│ │   ▮▮▮▮▮▮▯▯▯  已用 51%    │ │   ▮▮▮▮▮▯▯ 已用 32%    │  │
│ │   0  25  50  75  100%    │ │   0  25  50  75  100%    │  │
│ │   距离重置   1小时 9分   │ │   距离重置 5天15小时40分 │  │
│ │   ● 2026-10-19 18:25 北京时间│ │ ● 2026-10-25 08:56 北京时间││
│ └──────────────────────────┘ └──────────────────────────┘  │
│ 额外点数 0  |  24 秒前更新                        ⟳    ⚙   │
└────────────────────────────────────────────────────────────┘
```

- 进度条是 **20 段竖条**（`ItemsControl` + `Rectangle`），按 `used_percent` 点亮前 N 段。
- 配色：5 小时卡薄荷绿 `#4ADEB4`，本周卡雾蓝 `#7EB3F0`；`used_percent >= 80` 转橙 `#F0A860`，
  `>= 100` 转红 `#EF6B6B`。
- 字体：微软雅黑（中文）+ Segoe UI（数字/英文），无外部字体依赖。
- 无数据时整块区域显示"未检测到 Codex 用量数据"，副标题位置提示期望路径。
- `已同步` 状态判定：快照年龄 ≤ 120 秒 → 绿点 + "已同步"；> 120 秒 → 灰点 + "快照滞后"；
  无数据 → 该行整体不显示。不做任何"猜测当前用量"的处理（见 §10.3）。
- ⟳ 手动强制重扫；⚙ 用资源管理器打开 settings.json 所在目录。

## 7. 错误处理

| 情况 | 处理 |
|---|---|
| `~/.codex/sessions` 不存在 | 面板显示无数据文案 + 期望路径；不写日志文件，`Debug.WriteLine` |
| 文件被 Codex 独占（`IOException`） | 同一文件重试 1 次（间隔 50 ms），失败则跳到列表下一个候选 |
| JSON 片段解析失败 | 视为该条记录无效，继续向前找；全部失败 → 无数据 |
| `resets_at` 早于当前时间 | 倒计时显示"即将重置"，进度条按 100% 渲染 |
| 钩子注册失败（返回 `IntPtr.Zero`） | 托盘菜单项变灰 + 气泡提示"全局热键不可用"，程序继续以托盘模式运行 |
| settings.json 损坏 | 备份为 `.bak` 后用默认值重建 |
| 开机自启写入失败 | 菜单项保持未勾选并气泡提示原因，不崩溃 |

## 8. 测试

### 8.1 自动化（xUnit，只覆盖解析层）

`tests/CodexUsagePanel.Tests/`，fixture 为手工裁剪的真实 jsonl 片段：

1. 正常记录 → 两个窗口、百分比、`resets_at`、credits 全部正确
2. 文件内无任何 `rate_limits` → 返回 null
3. 尾部 256 KB 未命中但 4 MB 内命中 → 正确读取（验证扩大窗口逻辑）
4. 只有 `premium`（primary/secondary 全 null）→ 返回 null（验证 3.4 选择规则）
5. `credits` 为 null、`plan_type` 为 null → 不抛异常，走回退
6. 多文件多日期目录 → 选中 `LastWriteTime` 最新的那个
7. 模拟文件被占用 → 重试后跳到下一个候选
8. 缓存：同一 `(路径, mtime, 长度)` 第二次调用不重新读盘（用注入的读计数器断言）
9. 大文件：构造 > 4 MB 的 fixture，断言只读取尾部且耗时 < 50 ms

`DoubleCtrlHook`、`ClickAwayHook`、`PanelWindow` 不做自动化测试（依赖真实桌面输入），
改由 8.2 手动清单验收。

### 8.2 手动验收清单（交付时给用户逐条勾选）

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
- [ ] 删除 `~/.codex/sessions` 场景下面板显示无数据文案而非崩溃
- [ ] 面板弹出到出数据 < 150 ms
- [ ] 资源：面板隐藏静置 60 秒，CPU 增量 < 0.06 s；工作集 ≤ 80 MB（见 4.0）

## 9. 技术选型与构建

- .NET 8，WPF（`net8.0-windows`）。本机已确认 `dotnet 8.0.425`，无 Rust 工具链。
- NuGet：`Hardcodet.NotifyIcon.Wpf`（托盘）。除此之外不引第三方库。
- 项目结构：

```
E:\Viding\tip page\
  CodexUsagePanel.sln
  src\CodexUsagePanel\
    App.xaml / App.xaml.cs
    Data\{CodexUsageReader.cs, UsageSnapshot.cs}
    Input\{DoubleCtrlHook.cs, ClickAwayHook.cs, NativeInterop.cs}
    Ui\{PanelWindow.xaml, PanelWindow.xaml.cs, PanelViewModel.cs, Theme.xaml}
    Tray\TrayManager.cs
    Settings\AppSettings.cs
  tests\CodexUsagePanel.Tests\
    CodexUsageReaderTests.cs
    fixtures\*.jsonl
  docs\superpowers\specs\
```

- 交付命令：
  - 常规：`dotnet publish src\CodexUsagePanel -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true`
    → 单 exe 数 MB，需 .NET 8 Desktop Runtime（本机已具备）；R2R 减少启动 JIT 开销
  - 拷到无运行时机器：追加 `--self-contained true -p:PublishSingleFile=true`

## 10. 已知风险

1. **双击 Ctrl 误触发**：快速连按 Ctrl+C 有概率命中。缓解：400 ms 窗口 + 自动重复过滤 +
   托盘"暂停热键"。若使用中不稳，后续可换显式组合键。
2. **Codex 改字段格式**：解析层是单一入口（`CodexUsageReader`），格式变更只改一处；
   解析失败降级为"无数据"而不是崩溃。
3. **本地快照滞后**：数据是 Codex 上次请求时写入的，长时间不用后再看，百分比可能是几小时前的。
   面板用"N 秒前更新"如实标出快照年龄，不做静默猜测。
4. **143 MB 单文件**：已用尾部读取规避；若未来出现单条 `rate_limits` 记录本身超大，
   4 MB 窗口上限会命中"无数据"分支，属可接受降级。
