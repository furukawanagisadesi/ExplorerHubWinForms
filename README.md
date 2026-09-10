# ExplorerHubWinForms

一个基于 Windows Forms 的多标签页资源管理器。它会监视系统里新打开的资源管理器 (explorer.exe) 窗口，并把它们“吸收”为程序内的原生标签页；点关闭按钮会隐藏到系统托盘后台继续运行，最小化则正常缩到任务栏。

## 功能特性

- **原生多标签页**：每个标签页内嵌一个原生 `ExplorerBrowser`。
- **吸收资源管理器窗口**：自动捕获系统里新打开的 explorer 窗口并转成标签页。
- **控制面板排除**：控制面板窗口不会被吸收或关闭。
- **导航工具栏**：后退 / 前进 / 上一级 / 刷新 / 地址栏。
- **快捷键**：`Ctrl+T` 新建标签页，`Ctrl+W` 关闭标签页。
- **系统托盘**：点关闭按钮只隐藏到托盘（最小化仍缩到任务栏），仅从托盘菜单“退出”才真正结束进程。

## 技术栈

- .NET 8 (`net8.0-windows`)
- Windows Forms (`UseWindowsForms`)
- NuGet 包：[`WindowsAPICodePack`](https://www.nuget.org/packages/WindowsAPICodePack) 8.0.6（提供 `ExplorerBrowser` / `ShellObject` / `Shell` 等）
  - **固定在 8.0.6**：
    - 8.0.9 起 `ExplorerBrowser` 的 `ICommDlgBrowser3.IncludeObject` 每个条目都会触发 `FireContentChanged() → UpdateSearchState() → Items`，而 `Items` 每次访问都重建整个项目集合；`IncludeObject` 逐条目调用，于是大目录（如 `C:\Windows\System32`）退化为 O(N²)，界面假死。
    - 8.0.15 起 `PreFilterMessage` 增加了目标/焦点判断，使 shell 快捷键（Ctrl+C/Ctrl+V 等）不再下发，复制粘贴失效。
  - 升级前请务必验证：复制粘贴可用、大目录导航不卡死。

## 构建

```bash
dotnet build
```

## 文件结构

```
ExplorerHubWinForms/
├── ExplorerHubWinForms.csproj     # .NET 8 WinForms 项目，引用 WindowsAPICodePack
├── Program.cs                     # 入口：异常处理兜底
├── MainForm.cs                    # 主窗体：TabControl + 工具栏 + 托盘 + 接线
├── ExplorerTabControl.cs          # 自定义 TabControl：处理标签页双击关闭
├── ExplorerTabPage.cs             # 单个标签页：导航工具栏 + 原生 ExplorerBrowser
└── ExplorerWindowWatcher.cs       # 监视并“吸收”资源管理器窗口的核心类
```

## 使用说明

运行程序后，新打开的资源管理器窗口会被自动融入标签页。你可以在主界面通过“吸收资源管理器窗口”开关决定是否启用该行为。
