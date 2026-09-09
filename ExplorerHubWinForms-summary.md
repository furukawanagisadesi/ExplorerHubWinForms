# ExplorerHubWinForms — 项目摘要（供 Agent 使用）

> 用途：把一个 WinForms 的“多标签页资源管理器”的现状、结构、关键逻辑和已做的修改整理成一份可被另一个 Agent 直接消费的说明。
> 生成时点：在完成“不捕获控制面板”的修改之后、未采纳“启动时吸收已有窗口”改动的状态。

## 1. 项目概览

- **名称**：ExplorerHubWinForms
- **类型**：Windows 桌面程序（`WinExe`），基于 WinForms + Windows API Code Pack
- **目标框架**：`net8.0-windows`
- **NuGet**：`WindowsAPICodePack` 8.0.15.2（提供 `ExplorerBrowser` / `ShellObject` 等）
- **核心功能**：把系统中新建的资源管理器（explorer.exe）窗口“吸收”成程序内的原生标签页，形成多标签资源管理器体验。支持最小化/关闭到系统托盘后台运行。

## 2. 文件结构

```
ExplorerHubWinForms/
├── ExplorerHubWinForms.csproj     # .NET 8 WinForms 项目，引用 WindowsAPICodePack
├── Program.cs                     # 入口：异常处理兜底
├── MainForm.cs                    # 主窗体：TabControl + 工具栏 + 托盘 + 接线
├── ExplorerTabControl.cs          # 自定义 TabControl：处理标签页双击关闭
├── ExplorerTabPage.cs             # 单个标签页：导航工具栏 + 原生 ExplorerBrowser
└── ExplorerWindowWatcher.cs       # 监视并“吸收”资源管理器窗口的核心类（已含控制面板排除）
```

## 3. 各文件职责与关键逻辑

### 3.1 `ExplorerHubWinForms.csproj`
- `OutputType=WinExe`，`TargetFramework=net8.0-windows`，`UseWindowsForms=true`，`Nullable=enable`，`ImplicitUsings=enable`。
- 唯一第三方引用：`WindowsAPICodePack` 8.0.15.2。

### 3.2 `Program.cs`（入口）
- `[STAThread] Main()`，调用 `ApplicationConfiguration.Initialize()`。
- 设置全局异常兜底：`UnhandledExceptionMode.CatchException` + `ThreadException` + `AppDomain.UnhandledException`。
- `ShowError`：若异常是 `Microsoft.WindowsAPICodePack.Controls.CommonControlException` 则直接忽略（这类错误是 ExplorerBrowser 无法浏览某些 shell 对象如控制面板 `.cpl` 小程序导致，它们本就不是文件夹）；其它异常弹 `MessageBox`。

### 3.3 `MainForm.cs`（主窗体）
- 成员：
  - `ExplorerTabControl _tabs`（内容区，`Dock=Fill`）
  - `ExplorerWindowWatcher _watcher = new()`
  - `ToolStripButton _absorbToggle`（“吸收资源管理器窗口”开关，默认开启）
  - `NotifyIcon _tray`
  - `bool _exiting`
- 顶部工具栏按钮：新建标签页、关闭标签页、分隔符、吸收开关。
- `_absorbToggle.CheckedChanged`：勾选 → `_watcher.Start()`；取消 → `_watcher.Stop()`。
- 关闭行为：点关闭按钮只 `Hide()` 到托盘（`FormClosing` 里 `e.Cancel = true`），从托盘菜单“退出”才真正退出（`_exiting=true`）。
- 窗口最小化时 `Hide()` 并弹气泡提示。
- 双击标签关闭（监听 `_tabs.TabDoubleClicked`）。
- `AddTab(ShellObject? target)`：新建 `ExplorerTabPage`（默认“此电脑”）。
- `OnWindowAbsorbed`：把 `ParsingName` 转成 `ShellObject` 并新增标签页，然后 `ShowMainWindow()`；`file:///` URL 会转成本地路径；转换/识别失败则回退到“此电脑”。
- `ShowMainWindow()`：若没有标签页则新建一个，然后显示并激活主窗。
- `ExitApplication()`：`_exiting=true`、隐藏托盘、`Close()`。
- `Dispose`：取消事件、释放 watcher 和托盘。

### 3.4 `ExplorerTabControl.cs`
- 继承 `TabControl`，重写 `WndProc` 处理 `WM_LBUTTONDBLCLK`（0x0203），因为 TabControl 会把标签头上的双击事件内部消化。
- 命中某标签矩形即触发 `TabDoubleClicked(index)` 事件。
- 公开事件：`event EventHandler<int> TabDoubleClicked`。

### 3.5 `ExplorerTabPage.cs`（单个标签页）
- 继承 `TabPage`。
- 持有：`ExplorerBrowser _browser`（`Dock=Fill`）、导航工具栏（后退/前进/上一级/刷新/地址框）。
- 构造时接收 `ShellObject? initialTarget`，默认会导航到目标；无法导航时静默（`showError=false`）。
- `TryNavigate(target, showError)`：包裹 `ExplorerBrowser.Navigate`，`CommonControlException` 等会被捕获，`showError=true` 时弹窗。
- `UpdateNavigationState()`：根据 `NavigationLog` 刷新后退/前进/上一级按钮状态和标签标题/地址。
- 地址框回车：把文本 `ShellObject.FromParsingName` 后导航，失败弹窗。
- 刷新：重新导航到当前目录（`NavigateLogLocation` 到相同索引是空操作，需重导航）。
- `Dispose`：取消 `NavigationLogChanged` / `NavigationComplete` 事件。

### 3.6 `ExplorerWindowWatcher.cs`（核心：监视并吸收 explorer 窗口）
> ⚠️ 这是本项目中“吸收窗口”的唯一入口，也是最近被修改的地方。

**整体机制**：
- 通过 `Shell.Application.Windows()` 枚举系统的 explorer shell 窗口。
- 触发方式：前台切换事件钩子（`EVENT_SYSTEM_FOREGROUND`，`WineventOutofcontext`）+ 3 秒低频轮询兜底。
- 前台钩子触发后：防抖 80ms → `Poll(absorbNew: true)`；若还没吸收到（窗口尚未进入 `Shell.Windows()`），每 50ms 重试，最多约 1 秒（`_retriesLeft=20`）。

**关键成员**：
- `ExplorerPath`：`%Windows%\explorer.exe`。
- `ControlPanelClsid = "26EE0668-A00A-44D7-9371-BEB064C98683"`（控制面板 CLSID，本次新增）。
- `WinEventHook`、`_debounceTimer`(80ms)、`_fallbackTimer`(3000ms)、`HashSet<long> _seen`、`dynamic _shell`、`_retriesLeft`。
- 事件：`event EventHandler<ExplorerWindowAbsorbedEventArgs> WindowAbsorbed`。

**生命周期**：
- `Start()`：启动 fallback timer；若非空则挂前台钩子。
- `Stop()`：停两个 timer、卸载钩子。
- `Dispose()`：`Stop()` + 释放 timer + `Marshal.ReleaseComObject(_shell)`。

**构造函数**：只创建两个 timer 和委托，**不预登记窗口**。

**`Poll(bool absorbNew)`**：枚举 `Shell.Windows()`，对每个窗口：
1. `IsExplorerWindow`：`FullName == ExplorerPath` 且 `HWND != 0`，否则跳过。
2. **`IsControlPanel(window)`（本次新增）**：若是控制面板 → `continue`，不吸收、不关闭、不登记。
3. `current.Add(hwnd)`；`isNew = _seen.Add(hwnd)`。
4. 若 `absorbNew && isNew`：取 `GetParsingName`，调用 `window.Quit()` 关闭该窗口，`absorbedCount++`，触发 `WindowAbsorbed`。
5. 循环结束 `_seen.IntersectWith(current)` 清理已消失句柄。

**`IsControlPanel(dynamic window)`（本次新增）**：
- 优先用 `GetParsingName(window)`（即 `LocationURL` 或 `Document.Folder.Self.Path`），判断是否包含 `ControlPanelClsid`（不区分大小写）。该 CLSID 与系统显示语言无关，最可靠。
- 兜底：`window.Document.Folder.Self.Name` 是否为 `"Control Panel"` 或 `"控制面板"`。

**`GetParsingName(dynamic window)`**：
- 优先 `window.LocationURL`（普通文件夹是 `file:///` URL）。
- 为空/抛错则回退 `window.Document.Folder.Self.Path`（控制面板等非文件系统位置返回 `::{GUID}` 形式）。

**`IsExplorerWindow(dynamic window, out long hwnd)`**：取 `FullName` 与 `ExplorerPath` 比较 + 取 `HWND` 转 long。

**`Shell` 属性**：懒加载 `Type.GetTypeFromProgID("Shell.Application")` 实例。

**`OnForegroundChanged`**：`idObject==ObjidWindow && idChild==0 && hwnd!=0` 且 `IsExplorerProcess(hwnd)`（进程名是 explorer）且 `_seen` 不包含该 hwnd 时，设置 `_retriesLeft=20` 并启动防抖 timer。

**`IsExplorerProcess(IntPtr hwnd)`**：`GetWindowThreadProcessId` 取 PID，判断进程名是否为 `explorer`。

## 4. 当前已知行为 / 注意点

- **只吸收“新出现”的窗口**：`Poll(absorbNew: true)` 由前台钩子 / fallback timer 驱动，且 `_seen` 会去重。**启动时已存在的 explorer 窗口不会被吸收**（构造函数不再预登记，但也并未主动吸收旧窗口）。如果需求是我方当时讨论的“启动时吸收已有窗口”，那一步**尚未实现**。
- **控制面板不吸收**（本次已实现）：控制面板窗口既不会被 `Quit()` 关闭，也不会变成标签页，保持原窗口正常存在。
- **标签页默认打开“此电脑”**。
- **`.cpl` 或非文件夹 shell 位置**：`ExplorerBrowser.Navigate` 会抛 `CommonControlException`，`Program.cs` 全局已忽略，`ExplorerTabPage.TryNavigate` 也捕获。
- **常驻托盘**：关闭/最小化只是隐藏，仅托盘菜单“退出”真正退出。
- **空标签页时**：`CloseTab` 若没有标签页则 `Hide()` 到托盘继续后台吸收。
- 工程在 `D:\Program\CSharp\WorkProject\ExplorerHubWinForms`，可用 `dotnet build` 编译（`net8.0-windows`）。

## 5. 关键数据流（吸收一个 explorer 窗口）

```
explorer.exe 新窗口/前台切换
   → SetWinEventHook (EVENT_SYSTEM_FOREGROUND) 触发 OnForegroundChanged
   → 防抖 80ms → Poll(absorbNew:true)
   → Shell.Windows() 枚举 → IsExplorerWindow 通过
   → IsControlPanel? 是→跳过（不吸收）  否→继续
   → _seen.Add(hwnd) 判 isNew → GetParsingName → window.Quit() 关闭原窗口
   → 触发 WindowAbsorbed → MainForm.OnWindowAbsorbed
   → parsingName 转 ShellObject（file:// → LocalPath）→ AddTab() → ShowMainWindow()
```

## 6. 待办/潜在改进（由讨论得出，尚未实施）

- **启动时吸收已存在的 explorer 窗口**：当前行为是只吸收“之后出现”的窗口。若需要，应删除构造函数里的“预登记”逻辑并把首次 `Poll` 放到 `WindowAbsorbed` 订阅之后再以 `absorbNew: true` 执行（即在 `_watcher.Start()` 内调用一次 `Poll(absorbNew: true)`）。**此改动用户明确说“不用了”，未落地。**
