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
- NuGet 包：[`WindowsAPICodePack`](https://www.nuget.org/packages/WindowsAPICodePack) 8.0.15.2（提供 `ExplorerBrowser` / `ShellObject` / `Shell` 等）

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
