using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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
                Text = string.IsNullOrEmpty(location.Name) ? "新标签页" : location.Name;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"刷新导航状态失败: {ex}");
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

        // ftp:// 在内嵌 ExplorerBrowser 里不可靠: 不带用户名时视图为空, 且进入子目录
        // (尤其是含中文/特殊字符的目录)也会显示空白。因此不在本程序内嵌入, 直接交给
        // 系统资源管理器打开; 同时 ExplorerWindowWatcher 也不会吸收 FTP 窗口。
        if (IsFtpUrl(text, out var ftpUri))
        {
            OpenFtpInExplorer(ftpUri);
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
    /// 把 FTP 地址交给系统资源管理器浏览(内嵌 ExplorerBrowser 浏览 FTP 的子目录/中文名
    /// 会显示空白)。若地址里没有账号密码, 先用 Windows 原生凭据框收集, 再用带凭据的
    /// URL 打开, 避免资源管理器先匿名登录失败、弹出"无法登录到该 FTP 服务器"的提示。
    /// </summary>
    private void OpenFtpInExplorer(Uri uri)
    {
        var target = uri;
        if (string.IsNullOrEmpty(uri.UserInfo) || !uri.UserInfo.Contains(':'))
        {
            if (!TryPromptForCredentials(uri.Host, GetFtpUserName(uri), out var userName, out var password))
            {
                return;
            }

            target = new UriBuilder(uri)
            {
                UserName = userName,
                Password = password,
            }.Uri;
        }

        OpenInExplorer(target.AbsoluteUri);
    }

    private static string GetFtpUserName(Uri uri)
    {
        var userInfo = uri.UserInfo;
        var separator = userInfo.IndexOf(':');
        var user = separator >= 0 ? userInfo[..separator] : userInfo;
        return Uri.UnescapeDataString(user);
    }

    /// <summary>交给系统资源管理器打开该地址。</summary>
    private void OpenInExplorer(string url)
    {
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
    /// 弹出 Windows 原生的凭据输入框(与"Windows 安全中心"同款)收集 FTP 账号密码。
    /// 用户取消时返回 false。
    /// </summary>
    private bool TryPromptForCredentials(string host, string initialUserName, out string userName, out string password)
    {
        userName = string.Empty;
        password = string.Empty;

        var info = new CREDUI_INFO
        {
            cbSize = Marshal.SizeOf<CREDUI_INFO>(),
            hwndParent = FindForm()?.Handle ?? Handle,
            pszCaptionText = "登录 FTP",
            pszMessageText = $"请输入 FTP 服务器 {host} 的账号和密码",
            hbmBanner = IntPtr.Zero,
        };

        var authPackage = 0u;
        var save = false;
        var result = CredUIPromptForWindowsCredentials(
            ref info,
            0,
            ref authPackage,
            IntPtr.Zero,
            0,
            out var outBuffer,
            out var outBufferSize,
            ref save,
            CREDUIWIN_GENERIC);

        if (result != 0)
        {
            // 用户取消(ERROR_CANCELLED)或调用失败, 都不处理。
            return false;
        }

        try
        {
            var userLength = 256;
            var domainLength = 256;
            var passwordLength = 256;
            var userBuffer = new StringBuilder(userLength);
            var domainBuffer = new StringBuilder(domainLength);
            var passwordBuffer = new StringBuilder(passwordLength);

            if (!CredUnPackAuthenticationBuffer(
                    0,
                    outBuffer,
                    outBufferSize,
                    userBuffer,
                    ref userLength,
                    domainBuffer,
                    ref domainLength,
                    passwordBuffer,
                    ref passwordLength))
            {
                return false;
            }

            userName = userBuffer.ToString();
            if (string.IsNullOrEmpty(userName))
            {
                userName = initialUserName;
            }

            password = passwordBuffer.ToString();
            return true;
        }
        finally
        {
            if (outBuffer != IntPtr.Zero)
            {
                LocalFree(outBuffer);
            }
        }
    }

    private const int CREDUIWIN_GENERIC = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDUI_INFO
    {
        public int cbSize;
        public IntPtr hwndParent;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszMessageText;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszCaptionText;
        public IntPtr hbmBanner;
    }

    [DllImport("credui.dll", CharSet = CharSet.Unicode)]
    private static extern int CredUIPromptForWindowsCredentials(
        ref CREDUI_INFO pUiInfo,
        int dwAuthError,
        ref uint pulAuthPackage,
        IntPtr pvInAuthBuffer,
        uint ulInAuthBufferSize,
        out IntPtr ppvOutAuthBuffer,
        out uint pulOutAuthBufferSize,
        ref bool pfSave,
        int dwFlags);

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredUnPackAuthenticationBuffer(
        int dwFlags,
        IntPtr pAuthBuffer,
        uint cbAuthBuffer,
        StringBuilder pszUserName,
        ref int pcchMaxUserName,
        StringBuilder pszDomainName,
        ref int pcchMaxDomainName,
        StringBuilder pszPassword,
        ref int pcchMaxPassword);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

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
