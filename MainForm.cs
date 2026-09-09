using Microsoft.WindowsAPICodePack.Shell;

namespace ExplorerHubWinForms;

/// <summary>
/// 主窗体: 原生 TabControl, 每个标签页承载一个原生 ExplorerBrowser。
/// 支持双击标签关闭、最小化/关闭到托盘后台运行。
/// </summary>
public sealed class MainForm : Form
{
    private readonly ExplorerTabControl _tabs;
    private readonly ExplorerWindowWatcher _watcher = new();
    private readonly ToolStripButton _absorbToggle;
    private readonly NotifyIcon _tray;
    private bool _exiting;

    public MainForm()
    {
        Text = "ExplorerHub";
        // 窗口大小取所有显示器(虚拟桌面)总区域的四分之一, 而非仅主屏。
        // SystemInformation.VirtualScreen 覆盖全部已连接显示器的逻辑桌面范围。
        Width = SystemInformation.VirtualScreen.Width / 2;
        Height = SystemInformation.VirtualScreen.Height / 2;
        MinimumSize = new Size(500, 350);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        _tabs = new ExplorerTabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(14, 4),
            ShowToolTips = true,
        };
        _tabs.TabDoubleClicked += OnTabDoubleClicked;

        var newTab = new ToolStripButton("新建标签页")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
        };
        var closeTab = new ToolStripButton("关闭标签页")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
        };
        _absorbToggle = new ToolStripButton("吸收资源管理器窗口")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            CheckOnClick = true,
            Checked = true,
        };

        newTab.Click += (_, _) => AddTab(null);
        closeTab.Click += (_, _) => CloseCurrentTab();
        _absorbToggle.CheckedChanged += (_, _) =>
        {
            if (_absorbToggle.Checked)
            {
                _watcher.Start();
            }
            else
            {
                _watcher.Stop();
            }
        };

        var toolStrip = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            RenderMode = ToolStripRenderMode.System,
        };
        toolStrip.Items.Add(newTab);
        toolStrip.Items.Add(closeTab);
        toolStrip.Items.Add(new ToolStripSeparator());
        toolStrip.Items.Add(_absorbToggle);

        Controls.Add(_tabs);
        Controls.Add(toolStrip);

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "ExplorerHub",
            Visible = true,
        };
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("显示主窗口", null, (_, _) => ShowMainWindow());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("退出", null, (_, _) => ExitApplication());
        _tray.ContextMenuStrip = trayMenu;
        _tray.DoubleClick += (_, _) => ShowMainWindow();

        KeyDown += OnMainKeyDown;
        Resize += OnMainResize;
        FormClosing += OnMainFormClosing;
        _watcher.WindowAbsorbed += OnWindowAbsorbed;

        AddTab(null);
        _watcher.Start();
    }

    private static ShellObject ComputerFolder
    {
        get
        {
            var computer = ShellObject.FromParsingName(KnownFolders.Computer?.ParsingName ?? string.Empty);
            if (computer != null)
            {
                return computer;
            }

            // 兜底: 取不到“此电脑”时退回系统根目录, 避免启动即抛异常崩溃。
            var fallbackDesktop = ShellObject.FromParsingName(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            if (fallbackDesktop != null)
            {
                return fallbackDesktop;
            }

            throw new InvalidOperationException("无法打开任何 shell 文件夹");
        }
    }

    public ExplorerTabPage AddTab(ShellObject? target)
    {
        var tab = new ExplorerTabPage(target ?? ComputerFolder);
        _tabs.TabPages.Add(tab);
        _tabs.SelectedTab = tab;
        return tab;
    }

    private void OnWindowAbsorbed(object? sender, ExplorerWindowAbsorbedEventArgs e)
    {
        // 程序正在退出时不再吸收, 否则会对已关闭的窗体调用 Show/Hide 引发 ObjectDisposedException。
        if (_exiting || IsDisposed || Disposing)
        {
            return;
        }

        ShellObject? target = null;
        var parsingName = e.ParsingName;

        if (!string.IsNullOrWhiteSpace(parsingName))
        {
            // 普通文件夹给的是 file:/// URL, 需要转成本地路径; ::{GUID} 形式的解析名直接用。
            if (parsingName.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    parsingName = new Uri(parsingName).LocalPath;
                }
                catch
                {
                    // 转换失败则保持原样
                }
            }

            try
            {
                target = ShellObject.FromParsingName(parsingName);
            }
            catch
            {
                // 无法识别的地址, 退回到“此电脑”
            }
        }

        AddTab(target);
        ShowMainWindow();
    }

    private void CloseCurrentTab()
    {
        if (_tabs.SelectedTab is ExplorerTabPage tab)
        {
            CloseTab(tab);
        }
    }

    private void CloseTab(ExplorerTabPage tab)
    {
        _tabs.TabPages.Remove(tab);
        tab.Dispose();

        if (_tabs.TabPages.Count == 0)
        {
            // 没有标签页时, 隐藏到托盘继续后台运行(用于吸收资源管理器窗口)。
            Hide();
        }
    }

    private void OnTabDoubleClicked(object? sender, int index)
    {
        if (index >= 0 && index < _tabs.TabPages.Count && _tabs.TabPages[index] is ExplorerTabPage tab)
        {
            CloseTab(tab);
        }
    }

    private void OnMainKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.T)
        {
            AddTab(null);
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.W)
        {
            CloseCurrentTab();
            e.SuppressKeyPress = true;
        }
    }

    private void OnMainResize(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized && !_exiting && !IsDisposed && !Disposing)
        {
            Hide();
            _tray.ShowBalloonTip(1000, "ExplorerHub", "已最小化到后台运行", ToolTipIcon.Info);
        }
    }

    private void OnMainFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        // 点关闭按钮时隐藏到托盘, 而不是退出程序。
        e.Cancel = true;
        Hide();
    }

    private void ShowMainWindow()
    {
        if (_exiting || IsDisposed || Disposing)
        {
            return;
        }

        if (_tabs.TabPages.Count == 0)
        {
            AddTab(null);
        }

        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _exiting = true;
        _tray.Visible = false;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watcher.WindowAbsorbed -= OnWindowAbsorbed;
            _watcher.Dispose();
            _tray.Dispose();
        }

        base.Dispose(disposing);
    }
}
