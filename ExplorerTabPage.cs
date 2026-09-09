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
            Width = 520,
            ToolTipText = "输入路径后回车导航",
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
        });

        Controls.Add(_browser);
        Controls.Add(toolStrip);

        _back.Click += (_, _) => _browser.NavigateLogLocation(NavigationLogDirection.Backward);
        _forward.Click += (_, _) => _browser.NavigateLogLocation(NavigationLogDirection.Forward);
        _up.Click += (_, _) => NavigateUp();
        _refresh.Click += (_, _) => RefreshView();
        _address.KeyDown += OnAddressKeyDown;

        TryNavigate(initialTarget, showError: false);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _browser.NavigationLog.NavigationLogChanged -= OnNavigationLogChanged;
            _browser.NavigationComplete -= OnNavigationComplete;
        }

        base.Dispose(disposing);
    }
}
