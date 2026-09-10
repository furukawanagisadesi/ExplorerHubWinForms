using Microsoft.WindowsAPICodePack.Controls;
using Microsoft.WindowsAPICodePack.Controls.WindowsForms;
using Microsoft.WindowsAPICodePack.Shell;

namespace ExplorerHubWinForms;

/// <summary>
/// 单个标签页: 顶部导航工具栏 + 原生 IExplorerBrowser。
/// </summary>
public sealed class ExplorerTabPage : TabPage
{
    private readonly ExplorerBrowser _browser;
    private readonly ToolStripButton _back;
    private readonly ToolStripButton _forward;
    private readonly ToolStripButton _up;
    private readonly ToolStripButton _refresh;
    private readonly ToolStripTextBox _address;
    private readonly ToolStripButton _copyPath;
    private bool _updatingAddressWidth;

    public ExplorerBrowser Browser => _browser;

    public ExplorerTabPage(ShellObject? initialTarget)
    {
        Padding = new Padding(0);
        UseVisualStyleBackColor = true;

        _browser = new ExplorerBrowser
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
        };
        _browser.NavigationLog.NavigationLogChanged += OnNavigationLogChanged;
        _browser.NavigationComplete += OnNavigationComplete;

        _back = new ToolStripButton("\u2190") { ToolTipText = "后退", Enabled = false };
        _forward = new ToolStripButton("\u2192") { ToolTipText = "前进", Enabled = false };
        _up = new ToolStripButton("\u2191") { ToolTipText = "上一级", Enabled = false };
        _refresh = new ToolStripButton("刷新") { ToolTipText = "刷新" };
        _address = new ToolStripTextBox
        {
            AutoSize = false,
            ToolTipText = "输入路径后回车导航",
        };
        _copyPath = new ToolStripButton("复制路径")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "复制当前路径到剪贴板",
        };

        var toolStrip = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            RenderMode = ToolStripRenderMode.System,
        };
        toolStrip.Items.AddRange(new ToolStripItem[]
        {
            _back,
            _forward,
            _up,
            _refresh,
            new ToolStripSeparator(),
            _address,
            new ToolStripSeparator(),   // 地址栏与复制按钮之间的空隙
            _copyPath,
        });

        Controls.Add(_browser);
        Controls.Add(toolStrip);

        toolStrip.Resize += (_, _) => UpdateAddressWidth(toolStrip);
        UpdateAddressWidth(toolStrip);

        _back.Click += (_, _) => _browser.NavigateLogLocation(NavigationLogDirection.Backward);
        _forward.Click += (_, _) => _browser.NavigateLogLocation(NavigationLogDirection.Forward);
        _up.Click += (_, _) => NavigateUp();
        _refresh.Click += (_, _) => RefreshView();
        _address.KeyDown += OnAddressKeyDown;
        _copyPath.Click += (_, _) => CopyCurrentPath();

        TryNavigate(initialTarget, showError: false);
    }

    /// <summary>
    /// 让地址栏宽度随窗口变化: 取工具栏内容区宽度的 3/4, 但不超过扣除导航/复制
    /// 按钮后剩余的可用宽度, 以免右侧按钮被挤进溢出菜单。
    /// </summary>
    private void UpdateAddressWidth(ToolStrip toolStrip)
    {
        // 设置宽度会触发布局; 虽然布局不会再触发 Resize, 仍用重入守卫让不变量显式化。
        if (_updatingAddressWidth)
        {
            return;
        }

        _updatingAddressWidth = true;
        try
        {
            var contentWidth = toolStrip.ClientSize.Width;
            if (contentWidth <= 0)
            {
                return;
            }

            // 用“首选宽度”而非实时 Width: 某项被放进溢出菜单时 Width 会失真,
            // GetPreferredSize 始终返回其正常布局下的宽度。
            var reserved = _address.Margin.Horizontal;
            foreach (ToolStripItem item in toolStrip.Items)
            {
                if (!ReferenceEquals(item, _address))
                {
                    reserved += item.GetPreferredSize(Size.Empty).Width + item.Margin.Horizontal;
                }
            }

            var available = contentWidth - toolStrip.Padding.Horizontal - reserved;
            if (available <= 0)
            {
                _address.Width = 1;
                return;
            }

            _address.Width = Math.Min((int)(contentWidth * 0.75), available);
        }
        finally
        {
            _updatingAddressWidth = false;
        }
    }

    /// <summary>
    /// 导航到指定位置。某些 shell 位置(如控制面板里的 .cpl 小程序)不是普通文件夹,
    /// ExplorerBrowser.Navigate 会抛 CommonControlException, 这里统一兜住。
    /// </summary>
    private void TryNavigate(ShellObject? target, bool showError)
    {
        if (target == null)
        {
            return;
        }

        try
        {
            _browser.Navigate(target);
        }
        catch (Exception ex)
        {
            if (showError)
            {
                MessageBox.Show(this, $"无法导航到该位置。\r\n\r\n{ex.Message}", "ExplorerHub",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    private void OnNavigationLogChanged(object? sender, NavigationLogEventArgs e)
    {
        UpdateNavigationState();
    }

    private void OnNavigationComplete(object? sender, NavigationCompleteEventArgs e)
    {
        UpdateNavigationState();
    }

    private void UpdateNavigationState()
    {
        var log = _browser.NavigationLog;
        _back.Enabled = log.CanNavigateBackward;
        _forward.Enabled = log.CanNavigateForward;

        var location = log.CurrentLocation;
        _up.Enabled = location?.Parent != null;

        if (location != null)
        {
            _address.Text = location.IsFileSystemObject ? location.ParsingName : location.Name;
            Text = string.IsNullOrEmpty(location.Name) ? "新标签页" : location.Name;
        }
    }

    private void NavigateUp()
    {
        TryNavigate(_browser.NavigationLog.CurrentLocation?.Parent, showError: true);
    }

    private void RefreshView()
    {
        // NavigateLogLocation(CurrentLocationIndex) 在目标索引等于当前索引时是空操作,
        // 这里重新导航到当前目录以达到刷新效果。
        TryNavigate(_browser.NavigationLog.CurrentLocation, showError: true);
    }

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter)
        {
            return;
        }

        e.SuppressKeyPress = true;

        var text = _address.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            TryNavigate(ShellObject.FromParsingName(text), showError: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法导航到 \"{text}\"\r\n\r\n{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// 把当前浏览位置复制到剪贴板。
    /// 优先用地址栏显示的文本(FileSystem 路径或 shell 显示名); 取不到则退出, 避免复制到空串。
    /// </summary>
    private void CopyCurrentPath()
    {
        var text = _address.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"复制路径失败。\r\n\r\n{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _browser.NavigationLog.NavigationLogChanged -= OnNavigationLogChanged;
            _browser.NavigationComplete -= OnNavigationComplete;

            // WindowsAPICodePack 的 ExplorerBrowser 在创建句柄时会把自己注册为
            // Application.AddMessageFilter(this)(IMessageFilter), 但 Dispose 并不会反过来
            // RemoveMessageFilter。Tint 关闭标签页后, 那个已释放的 ExplorerBrowser 仍留在
            // 全局消息过滤器链中, 消息循环泵下一帧时 IsMessageForExplorerBrowser() 访问
            // Control.Handle 就会抛 ObjectDisposedException。这里必须在释放控制前把它移除。
            Application.RemoveMessageFilter(_browser);
        }

        base.Dispose(disposing);
    }
}
