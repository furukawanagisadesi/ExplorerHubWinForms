using System.Diagnostics;
using System.Runtime.InteropServices;
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
    private string _displayName = "新标签页";
    private IReadOnlyList<string> _pathSegments = new[] { "新标签页" };
    private string _fullPath = string.Empty;
    private IReadOnlyList<string> _pendingSelection = Array.Empty<string>();

    public ExplorerBrowser Browser => _browser;

    /// <summary>当前浏览位置的叶子名, 供宿主决定标签标题。</summary>
    public string DisplayName => _displayName;

    /// <summary>当前路径自根到叶的各级名称; 非文件系统位置只有叶子名。用于同名标签逐级区分。</summary>
    public IReadOnlyList<string> PathSegments => _pathSegments;

    /// <summary>完整路径, 用作标签悬停提示。</summary>
    public string FullPath => _fullPath;

    /// <summary>当前浏览位置变化时触发, 供宿主重算所有标签标题。</summary>
    public event EventHandler? CurrentLocationChanged;

    public ExplorerTabPage(ShellObject? initialTarget, IReadOnlyList<string>? selectedPaths = null)
    {
        _pendingSelection = selectedPaths ?? Array.Empty<string>();

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
        ApplyPendingSelection();
    }

    /// <summary>
    /// 吸收“打开文件所在位置”这类窗口时, 导航完成后恢复源窗口的选中项。
    /// NavigationComplete 时视图已创建(库内部就是在这时取 GetCurrentViewMode), 这里再排到
    /// 消息队列末尾, 让视图先完成填充; 待选路径只尝试一次。
    /// </summary>
    private void ApplyPendingSelection()
    {
        if (_pendingSelection.Count == 0)
        {
            return;
        }

        var selection = _pendingSelection;

        try
        {
            _browser.BeginInvoke((MethodInvoker)(() =>
            {
                if (_browser.IsDisposed || _pendingSelection.Count == 0)
                {
                    return;
                }

                // 等真正导航到目标文件夹再选: ExplorerBrowser 初始化时的桌面视图会先完成一次导航。
                if (!ShellItemSelector.IsAtParentFolder(_browser, selection))
                {
                    return;
                }

                ShellItemSelector.TrySelect(_browser, selection);
                _pendingSelection = Array.Empty<string>();
            }));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"恢复选中项失败: {ex}");
        }
    }

    private void UpdateNavigationState()
    {
        // 本方法由 ExplorerBrowser 的 COM 事件回调直接调用, 某些 shell 项访问属性会抛异常;
        // 异常若跨 COM 边界返回会给 shell 一个失败 HRESULT 并可能让浏览器状态不一致, 这里全部兜住。
        try
        {
            var log = _browser.NavigationLog;
            _back.Enabled = log.CanNavigateBackward;
            _forward.Enabled = log.CanNavigateForward;

            var location = log.CurrentLocation;
            _up.Enabled = location?.Parent != null;

            if (location != null)
            {
                _address.Text = location.IsFileSystemObject ? location.ParsingName : location.Name;
                SetLocation(location);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"刷新导航状态失败: {ex}");
        }
    }

    /// <summary>
    /// 记录当前位置的显示信息并通知宿主。标题不在这里直接设置——同名的多个标签要放在
    /// 一起才能决定怎么区分, 统一交给 MainForm 计算。
    /// </summary>
    private void SetLocation(ShellObject location)
    {
        _displayName = string.IsNullOrEmpty(location.Name) ? "新标签页" : location.Name;
        _fullPath = (location.IsFileSystemObject ? location.ParsingName : location.Name)
            ?? string.Empty;
        _pathSegments = BuildPathSegments(location);
        CurrentLocationChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 把当前路径拆成各级名称。文件系统用 ParsingName(如 D:\a\b)按分隔符拆分;
    /// 控制面板等非文件系统位置没有稳定的路径层级, 只保留叶子名。
    /// </summary>
    private static IReadOnlyList<string> BuildPathSegments(ShellObject location)
    {
        if (location.IsFileSystemObject && !string.IsNullOrEmpty(location.ParsingName))
        {
            var parts = location.ParsingName.Split(
                new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                return parts;
            }
        }

        var name = string.IsNullOrEmpty(location.Name) ? "新标签页" : location.Name;
        return new[] { name };
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

        // ftp:// 交给系统资源管理器: 以地址栏方式新开一个资源管理器窗口打开该地址,
        // 后续(凭据输入、进入子目录等)全部由资源管理器负责, 与本程序无关。内嵌
        // ExplorerBrowser 浏览 FTP 不可靠(不带用户名视图为空, 子目录/中文名会空白);
        // ExplorerWindowWatcher 也不会吸收 FTP 窗口。
        if (IsFtpUrl(text, out var ftpUri))
        {
            OpenInExplorer(ftpUri.AbsoluteUri);
            // 地址栏恢复为当前实际位置, 避免残留外部打开的 FTP 地址。
            UpdateNavigationState();
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

    /// <summary>判断地址是否为 ftp 协议, 是则输出解析后的 Uri。</summary>
    private static bool IsFtpUrl(string text, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) ||
            !string.Equals(parsed.Scheme, "ftp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    /// <summary>
    /// 新开一个系统资源管理器窗口浏览该地址, 打开后与本程序无关。
    /// 必须走 Shell.Application.Open(即资源管理器地址栏那条导航路径), 不能用
    /// <c>explorer.exe "url"</c>: 后者打开同样的 FTP 视图和"登录身份"框, 但即使用户
    /// 填对账号密码也会报"用指定的用户名和密码无法登录到该FTP服务器"; 经实测只有
    /// 地址栏路径(Shell 导航) 或 Shell.Application.Open 能正常登录。
    /// </summary>
    private void OpenInExplorer(string url)
    {
        // 优先: 通过 Shell 以"地址栏"方式打开 FTP。COM 不可用时再退回 explorer.exe。
        object? shell = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType != null)
            {
                shell = Activator.CreateInstance(shellType);
                if (shell != null)
                {
                    ((dynamic)shell).Open(url);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"通过 Shell 打开失败, 回退 explorer.exe: {ex}");
        }
        finally
        {
            if (shell != null && Marshal.IsComObject(shell))
            {
                Marshal.ReleaseComObject(shell);
            }
        }

        try
        {
            var explorerPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(new ProcessStartInfo
            {
                FileName = explorerPath,
                Arguments = $"\"{url}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法在资源管理器中打开 \"{url}\"\r\n\r\n{ex.Message}", "错误",
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
