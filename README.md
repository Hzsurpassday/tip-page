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
