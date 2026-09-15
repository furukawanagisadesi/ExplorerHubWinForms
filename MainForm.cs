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
    private readonly Icon _appIcon;
    private bool _exiting;

    public MainForm()
    {
        Text = "ExplorerHub";
        // 窗体图标: 标题栏、运行中的任务栏按钮、Alt-Tab 缩略图。
        _appIcon = LoadAppIcon();
        Icon = _appIcon;
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
        _tabs.TabAreaDoubleClicked += OnTabAreaDoubleClicked;

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
            Icon = _appIcon,
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
        FormClosing += OnMainFormClosing;
        _watcher.WindowAbsorbed += OnWindowAbsorbed;

        AddTab(null);
        _watcher.Start();
    }

    private static ShellObject ComputerFolder
    {
        get
        {
            // 注意: 不能把 null 解析名并成空串再传入, FromParsingName("") 会抛异常,
            // 那样下面的桌面兜底就永远走不到。这里先判空。
            var computerPath = KnownFolders.Computer?.ParsingName;
            if (!string.IsNullOrEmpty(computerPath))
            {
                var computer = ShellObject.FromParsingName(computerPath);
                if (computer != null)
                {
                    return computer;
                }
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

    public ExplorerTabPage AddTab(ShellObject? target, IReadOnlyList<string>? selectedPaths = null)
    {
        var tab = new ExplorerTabPage(target ?? ComputerFolder, selectedPaths);
        tab.CurrentLocationChanged += OnTabLocationChanged;
        _tabs.TabPages.Add(tab);
        _tabs.SelectedTab = tab;
        UpdateTabTitles();
        return tab;
    }

    private void OnWindowAbsorbed(object? sender, ExplorerWindowAbsorbedEventArgs e)
    {
        // 程序正在退出时不再吸收, 否则会对已关闭的窗体调用 Show/Hide 引发 ObjectDisposedException。
        if (_exiting || IsDisposed || Disposing)
        {
            return;
        }

        // 本方法由 ExplorerWindowWatcher 的计时器回调驱动, 异常不能冒泡中断后续吸收。
        try
        {
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

            AddTab(target, e.SelectedPaths);
            ShowMainWindow();

            // 标签页已建好, 通知 watcher 可以安全关闭原资源管理器窗口。
            e.Absorbed = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"吸收资源管理器窗口失败: {ex}");
        }
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
        tab.CurrentLocationChanged -= OnTabLocationChanged;
        _tabs.TabPages.Remove(tab);
        tab.Dispose();

        if (_tabs.TabPages.Count == 0)
        {
            // 没有标签页时, 隐藏到托盘继续后台运行(用于吸收资源管理器窗口)。
            Hide();
        }

        UpdateTabTitles();
    }

    private void OnTabLocationChanged(object? sender, EventArgs e)
    {
        UpdateTabTitles();
    }

    /// <summary>
    /// 统一重算所有标签的标题与悬停提示。平时显示叶子名; 出现同名标签时, 从叶子往上
    /// 逐级追加父级, 取能让同名的一组标签互不相同的最小层级作为括注, 例如
    /// "2026-09-15 (慧医卡)" 与 "2026-09-15 (光大)"; 连完整路径都相同(同一文件夹开了
    /// 多个标签)时退化为序号。悬停提示始终是完整路径。
    /// </summary>
    private void UpdateTabTitles()
    {
        var pages = _tabs.TabPages.OfType<ExplorerTabPage>().ToList();

        foreach (var page in pages)
        {
            page.ToolTipText = page.FullPath;
        }

        foreach (var group in pages.GroupBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var members = group.ToList();
            if (members.Count == 1)
            {
                members[0].Text = members[0].DisplayName;
                continue;
            }

            var depth = FindDistinguishingDepth(members);
            if (depth < 0)
            {
                // 完整路径也一致, 按出现顺序编号兜底(第一个保持叶子名)。
                for (var i = 0; i < members.Count; i++)
                {
                    members[i].Text = i == 0
                        ? members[i].DisplayName
                        : $"{members[i].DisplayName} ({i + 1})";
                }

                continue;
            }

            // 优先只显示“最先区分开”的那一级祖先名; 若它们在组内仍会撞名, 就用完整祖先链。
            var hints = members.Select(m => AncestorAtLevel(m, depth)).ToList();
            var useChain = hints.Distinct(StringComparer.OrdinalIgnoreCase).Count() != hints.Count;

            for (var i = 0; i < members.Count; i++)
            {
                var suffix = useChain ? AncestorChain(members[i], depth) : hints[i];
                members[i].Text = $"{members[i].DisplayName} ({suffix})";
            }
        }
    }

    /// <summary>
    /// 找到能让本组标签互不相同的最小父级层级(1 表示只加一级父级)。找不到返回 -1。
    /// </summary>
    private static int FindDistinguishingDepth(List<ExplorerTabPage> members)
    {
        var maxDepth = members.Max(p => p.PathSegments.Count - 1);
        for (var depth = 1; depth <= maxDepth; depth++)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unique = true;
            foreach (var member in members)
            {
                if (!seen.Add(QualifiedName(member, depth)))
                {
                    unique = false;
                    break;
                }
            }

            if (unique)
            {
                return depth;
            }
        }

        return -1;
    }

    /// <summary>取路径末尾 depth+1 级(叶子 + depth 级祖先)拼成的限定名。</summary>
    private static string QualifiedName(ExplorerTabPage page, int depth)
    {
        var segments = page.PathSegments;
        var take = Math.Min(depth + 1, segments.Count);
        return string.Join("\\", segments.Skip(segments.Count - take));
    }

    /// <summary>取从叶子往上数第 depth 级的祖先名(即最先区分开的那一级)。</summary>
    private static string AncestorAtLevel(ExplorerTabPage page, int depth)
    {
        var segments = page.PathSegments;
        var index = segments.Count - 1 - depth;
        return index >= 0 && index < segments.Count ? segments[index] : page.DisplayName;
    }

    /// <summary>取路径末尾 depth 级祖先拼成的链, 用于祖先名本身仍会撞名时的兜底。</summary>
    private static string AncestorChain(ExplorerTabPage page, int depth)
    {
        var segments = page.PathSegments;
        var end = segments.Count - 1;
        var start = Math.Max(0, end - depth);
        return start >= end ? page.DisplayName : string.Join("\\", segments.Skip(start).Take(end - start));
    }

    private void OnTabDoubleClicked(object? sender, int index)
    {
        if (index >= 0 && index < _tabs.TabPages.Count && _tabs.TabPages[index] is ExplorerTabPage tab)
        {
            CloseTab(tab);
        }
    }

    private void OnTabAreaDoubleClicked(object? sender, EventArgs e)
    {
        AddTab(null);
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

    private void OnMainFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        // 系统关机/注销或任务管理器结束时必须放行, 否则会阻止会话结束。
        if (e.CloseReason is CloseReason.WindowsShutDown
            or CloseReason.TaskManagerClosing
            or CloseReason.ApplicationExitCall)
        {
            _exiting = true;
            return;
        }

        // 点关闭按钮时隐藏到托盘(缩略图标), 而不是退出程序。
        e.Cancel = true;
        Hide();
    }

    /// <summary>
    /// 处理单实例广播: 第二个实例启动时会 PostMessage 此消息, 这里把主窗口重新显示并激活。
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Program.ShowMainWindowMessageId)
        {
            ShowMainWindow();
            return;
        }

        if (m.Msg == 0x0203) // WM_LBUTTONDBLCLK
        {
            var p = m.LParam.ToInt64();
            var client = new Point((short)(p & 0xFFFF), (short)((p >> 16) & 0xFFFF));
            // 标签行“空白处”的双击投递到本窗体, 在此做命中判定。
            // lParam 是本窗体(客户区)坐标 → 换算到 _tabs 客户区坐标后再做判定。
            var screen = PointToScreen(client);
            _tabs.HandleTabDoubleClick(_tabs.PointToClient(screen));
            return;
        }

        base.WndProc(ref m);
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

    /// <summary>从嵌入资源加载多分辨率应用图标; 失败时退回系统默认图标。</summary>
    private static Icon LoadAppIcon()
    {
        using var stream = typeof(MainForm).Assembly
            .GetManifestResourceStream("ExplorerHubWinForms.app.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watcher.WindowAbsorbed -= OnWindowAbsorbed;
            _watcher.Dispose();
            _tray.Dispose();
            _appIcon.Dispose();
        }

        base.Dispose(disposing);
    }
}
