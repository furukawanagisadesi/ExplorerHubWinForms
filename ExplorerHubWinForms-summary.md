# ExplorerHubWinForms — 项目摘要（供 Agent 使用）

> 用途：把一个 WinForms 的“多标签页资源管理器”的现状、结构、关键逻辑和已做的修改整理成一份可被另一个 Agent 直接消费的说明。
> 生成时点：在完成“关闭标签页崩溃修复、吸收竞态加固、多屏窗口大小、单实例、缩小到任务栏/关闭到托盘、地址栏宽度自适应”之后的状态。

## 1. 项目概览

- **名称**：ExplorerHubWinForms
- **类型**：Windows 桌面程序（`WinExe`），基于 WinForms + Windows API Code Pack
- **目标框架**：`net8.0-windows`
- **NuGet**：`WindowsAPICodePack` 8.0.15.2（提供 `ExplorerBrowser` / `ShellObject` 等）
- **核心功能**：把系统中新建的资源管理器（explorer.exe）窗口“吸收”成程序内的原生标签页，形成多标签资源管理器体验。单实例运行；缩小按钮缩到任务栏；关闭按钮隐藏到系统托盘继续后台运行。

## 2. 文件结构

```
ExplorerHubWinForms/
├── ExplorerHubWinForms.csproj     # .NET 8 WinForms 项目，引用 WindowsAPICodePack
├── Program.cs                     # 入口：单实例互斥锁 + 异常处理兜底
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
- **单实例（本次新增）**：
  - 会话级命名 `Mutex`（`ExplorerHubWinForms_SingleInstance`，非 `Global\`），第二个实例拿不到锁时不会启动新进程。
  - 备用实例用 `PostMessage` + `HWND_BROADCAST` 广播自定义窗口消息 `ExplorerHub_ShowMainWindow`（`ShowMainWindowMessageId`，由 `RegisterWindowMessage` 取得，MainForm 通过 `WndProc` 接收），then 退出。
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
- 启动时：`AddTab(null)` 打开一个“此电脑”标签 + `_watcher.Start()`。
- **窗口大小（本次修改）**：`Width = SystemInformation.VirtualScreen.Width / 2`、`Height = ...Height / 2`。`VirtualScreen` 覆盖**所有显示器**的逻辑桌面总区域，窗口面积即全部显示器总面积的 1/4；“此电脑”失败时回退桌面目录（`ComputerFolder` 兜底，避免启动即崩溃）。
- **关闭行为**：点关闭按钮只 `Hide()` 隐藏到系统托盘（缩略图标），`FormClosing` 里 `e.Cancel = true`；从托盘菜单“退出”才真正退出（`_exiting=true`）。
- **缩小行为（本次修改）**：无自定义 `OnMainResize`（已移除），缩小按钮走默认行为，窗口正常缩到**任务栏**（不隐藏、不弹气泡）。
- **单实例接收（本次新增）**：重写 `WndProc`，捕获 `Program.ShowMainWindowMessageId`，调用 `ShowMainWindow()` 恢复并激活主窗口。
- **双击标签行空白处新建（本次新增）**：`WndProc` 中另处理 `WM_LBUTTONDBLCLK`（0x0203）——空白处双击会投递到本窗体（而非子控件），在此把本窗体客户区坐标经 `PointToScreen` → `PointToClient` 换算到 `_tabs` 客户区坐标，再调用 `_tabs.HandleTabDoubleClick(tabPoint)`。
- 双击标签关闭（监听 `_tabs.TabDoubleClicked`，由 `ExplorerTabControl` 触发）。
- `AddTab(ShellObject? target)`：新建 `ExplorerTabPage`（默认“此电脑”）。
- `OnWindowAbsorbed`：程序退出中（`_exiting || IsDisposed || Disposing`）则忽略；否则把 `ParsingName` 转成 `ShellObject` 并新增标签页，然后 `ShowMainWindow()`；`file:///` URL 会转成本地路径；转换/识别失败则回退到“此电脑”。
- `ShowMainWindow()`：若没有标签页则新建一个，然后显示并激活主窗；同上也做了退出中守卫。
- `CloseTab(tab)`：移除并 Dispose 标签页；若无标签页则 `Hide()` 到托盘继续后台吸收。
- `ExitApplication()`：`_exiting=true`、隐藏托盘、`Close()`。
- `Dispose`：取消事件、释放 watcher 和托盘。

### 3.4 `ExplorerTabControl.cs`
- 继承 `TabControl`，重写 `WndProc` 处理 `WM_LBUTTONDBLCLK`（0x0203），因为 TabControl 会把标签头上的双击事件内部消化。
- 公开事件：`event EventHandler<int> TabDoubleClicked`（双击标签头→关闭）、`event EventHandler TabAreaDoubleClicked`（双击标签行空白处→新建）。
- `HandleTabDoubleClick(Point clientPoint)`：双击命中判定（公共方法，两个窗口共用）。命中某标签矩形 → 触发 `TabDoubleClicked(index)`；否则若在标签行内（`y` 在标签行顶部与内容区上边缘之间，用 `DisplayRectangle.Top` 与 `GetTabRect(last).Top` 界定）→ 触发 `TabAreaDoubleClicked`。
- **关键（本次修复后）**：标签头上的双击直接投递到本控件（子窗口）的 `WndProc`，**不会**经过父窗体；而标签行"空白处"的双击投递到 `MainForm`。因此关闭逻辑在 `ExplorerTabControl.WndProc` 处理（它只收得到标签头双击），新建逻辑由 `MainForm.WndProc` 转向调用 `HandleTabDoubleClick`（它只收得到空白处双击），两者互补、不重复投递。

### 3.5 `ExplorerTabPage.cs`（单个标签页）
- 继承 `TabPage`。
- 持有：`ExplorerBrowser _browser`（`Dock=Fill`）、导航工具栏（后退/前进/上一级/刷新/地址框/复制路径）。
- 构造时接收 `ShellObject? initialTarget`，默认会导航到目标；无法导航时静默（`showError=false`）。
- `TryNavigate(target, showError)`：包裹 `ExplorerBrowser.Navigate`，`CommonControlException` 等会被捕获，`showError=true` 时弹窗。
- `UpdateNavigationState()`：根据 `NavigationLog` 刷新后退/前进/上一级按钮状态和标签标题/地址。
- 地址框回车：把文本 `ShellObject.FromParsingName` 后导航，失败弹窗。
- **地址框（`_address`，`ToolStripTextBox`）**：`AutoSize=false`，宽度随窗口自动调整。构造时订阅 `toolStrip.Resize` 并调用 `UpdateAddressWidth(toolStrip)`：宽度 = `min(工具栏内容区宽度 × 3/4, 内容区宽度 − 各按钮及边距占用)`。即大窗口下恒为窗口内容区的 3/4，窗口偏窄时自动收缩到刚好放下右侧按钮，避免“复制路径”被挤进溢出菜单。`UpdateAddressWidth` 通过遍历 `toolStrip.Items`（排除地址框本身）累加 `GetPreferredSize(Size.Empty).Width + Margin.Horizontal` 实算预留宽度，不写死常量；用首选宽度而非实时 `Width`，避免某项进入溢出菜单时宽度失真，并带重入守卫。
- **复制路径按钮（`_copyPath`）**：工具栏里地址框右侧，两者之间用 `ToolStripSeparator()` 留出空隙。点击调用 `CopyCurrentPath()`——把地址框当前显示的路径复制到剪贴板；文本为空则不做任何事，剪贴板访问失败时弹窗提示。
- 刷新：重新导航到当前目录（`NavigateLogLocation` 到相同索引是空操作，需重导航）。
- **`Dispose`（修复）**：除了取消 `NavigationLogChanged` / `NavigationComplete` 事件，还调用 `Application.RemoveMessageFilter(_browser)`——见下方第 4 节崩溃根因。

### 3.6 `ExplorerWindowWatcher.cs`（核心：监视并吸收 explorer 窗口）
> ⚠️ 这是本项目中“吸收窗口”的唯一入口。

**整体机制**：
- 通过 `Shell.Application.Windows()` 枚举系统的 explorer shell 窗口。
- 触发方式：前台切换事件钩子（`EVENT_SYSTEM_FOREGROUND`，`WineventOutofcontext`）+ 3 秒低频轮询兜底。
- 前台钩子触发后：防抖 80ms → `Poll(absorbNew: true)`；若还没吸收到（窗口尚未进入 `Shell.Windows()`），每 50ms 重试，最多约 1 秒（`_retriesLeft=20`）。

**关键成员**：
- `ExplorerPath`：`%Windows%\explorer.exe`。
- `ControlPanelClsid = "26EE0668-A00A-44D7-9371-BEB064C98683"`（控制面板 CLSID）。
- `WinEventHook`、`_debounceTimer`(80ms)、`_fallbackTimer`(3000ms)、`HashSet<long> _seen`、`dynamic _shell`、`_retriesLeft`、`bool _disposed`（本次新增）。
- 事件：`event EventHandler<ExplorerWindowAbsorbedEventArgs> WindowAbsorbed`。

**生命周期**：
- `Start()`：若已 `_disposed` 则直接返回；否则启动 fallback timer；若非空则挂前台钩子。
- `Stop()`：若已 `_disposed` 则直接返回；否则调用 `StopCore()`。
- `StopCore()`（本次拆分）：停两个 timer、卸载钩子。拆出私有方法是为了让 `Dispose` 在置位 `_disposed` 后仍能真正卸载钩子/计时器。
- `Dispose()`（本次加固）：先置位 `_disposed` 再 `StopCore()`、释放 timer、`Marshal.ReleaseComObject(_shell)`。

**构造函数**：创建两个 timer 和委托；启动时调用一次 `Poll(absorbNew: false)` 记录已存在窗口，避免把已有窗口也吸进来。

**`Poll(bool absorbNew)`**：枚举 `Shell.Windows()`，对每个窗口：
1. `IsExplorerWindow`：`FullName == ExplorerPath` 且 `HWND != 0`，否则跳过。
2. `IsControlPanel(window)`：若是控制面板 → `continue`，不吸收、不关闭、不登记。
3. `current.Add(hwnd)`；`isNew = _seen.Add(hwnd)`。
4. 若 `absorbNew && isNew`：取 `GetParsingName`，调用 `window.Quit()` 关闭该窗口，`absorbedCount++`，触发 `WindowAbsorbed`。
5. 循环结束 `_seen.IntersectWith(current)` 清理已消失句柄。

**`OnForegroundChanged`**：`idObject==ObjidWindow && idChild==0 && hwnd!=0` 且 `IsExplorerProcess(hwnd)`（进程名是 explorer）且 `_seen` 不包含该 hwnd 时，设置 `_retriesLeft=20` 并启动防抖 timer。`_disposed` 时直接返回（本次加固——防退出时仍有已排队的 WinEvent 回调访问已释放 Timer）。

**`IsControlPanel(dynamic window)`**：
- 优先用 `GetParsingName(window)`（即 `LocationURL` 或 `Document.Folder.Self.Path`），判断是否包含 `ControlPanelClsid`（不区分大小写）。该 CLSID 与系统显示语言无关，最可靠。
- 兜底：`window.Document.Folder.Self.Name` 是否为 `"Control Panel"` 或 `"控制面板"`。

**`GetParsingName(dynamic window)`**：
- 优先 `window.LocationURL`（普通文件夹是 `file:///` URL）。
- 为空/抛错则回退 `window.Document.Folder.Self.Path`（控制面板等非文件系统位置返回 `::{GUID}` 形式）。

**`IsExplorerWindow(dynamic window, out long hwnd)`**：取 `FullName` 与 `ExplorerPath` 比较 + 取 `HWND` 转 long。

**`Shell` 属性**：懒加载 `Type.GetTypeFromProgID("Shell.Application")` 实例。

**`IsExplorerProcess(IntPtr hwnd)`**：`GetWindowThreadProcessId` 取 PID，判断进程名是否为 `explorer`。

## 4. 崩溃根因与已做修复（关键）

### 4.1 关闭标签页抛 `ObjectDisposedException`（已修复，`ExplorerTabPage.cs`）
- **根因**：WindowsAPICodePack 的 `ExplorerBrowser` 在创建窗口句柄时把自己注册为 `Application.AddMessageFilter`（实现了 `IMessageFilter`），但其 `Dispose` 并不会反注册。关闭标签页 Dispose 该控件后，它仍留在 WinForms 全局消息过滤器链中；消息循环泵下一帧时 `ProcessFilters` 回调 `IsMessageForExplorerBrowser` → 访问已释放控件的 `Control.Handle` → 抛 `ObjectDisposedException`。
- **修复**：在 `ExplorerTabPage.Dispose(bool)` 释放控件前调用 `Application.RemoveMessageFilter(_browser)`。

### 4.2 吸收竞态与退出竞态（已加固）
- `ExplorerWindowWatcher` 用 `_disposed` 守卫 `Start`/`Stop`/`StopCore`/Tick 回调/`OnForegroundChanged`，防止退出时 WinEvent 排队回调触碰已 Dispose 的 `WinForms.Timer`。
- `MainForm.OnWindowAbsorbed` / `ShowMainWindow` / 托盘气泡，均加 `_exiting || IsDisposed || Disposing` 守卫，防止退出瞬间被吸收事件唤醒已关闭窗体。
- `ComputerFolder` 增加桌面目录兜底，避免 `KnownFolders.Computer` 为 null 时启动即抛异常。

## 5. 当前已知行为 / 注意点

- **单实例**：第二个实例不启动，只把已运行实例的主窗口显示并激活，然后退出。用会话级 Mutex（非 `Global\`）。
- **只吸收“新出现”的窗口**：`Poll(absorbNew: true)` 由前台钩子 / fallback timer 驱动，且 `_seen` 去重。**启动时已存在的 explorer 窗口不会被吸收**（构造函数记录已有窗口，已落地；需求“启动时吸收已有窗口”用户已明确说“不用了”）。
- **控制面板不吸收**：控制面板窗口既不会被 `Quit()` 关闭，也不会变成标签页，保持原窗口正常存在。
- **标签页默认打开“此电脑”**（失败退回桌面目录）。
- **`.cpl` 或非文件夹 shell 位置**：`ExplorerBrowser.Navigate` 会抛 `CommonControlException`，`Program.cs` 全局已忽略，`ExplorerTabPage.TryNavigate` 也捕获。
- **常驻托盘**：关闭按钮隐藏到托盘缩略图标；缩小按钮缩到任务栏；仅托盘菜单“退出”真正退出。
- **空标签页时**：`CloseTab` 若没有标签页则 `Hide()` 到托盘继续后台吸收。
- 工程在 `D:\CSharp\WorkProject\ExplorerHubWinForms`，可用 `dotnet build` 编译（`net8.0-windows`）。
- 若编译报“文件被占用”，说明有运行中的实例（单实例进程），先 `Stop-Process-Name ExplorerHubWinForms` 再编译。

## 6. 关键数据流（吸收一个 explorer 窗口）

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
