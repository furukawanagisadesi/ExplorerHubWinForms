using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ExplorerHubWinForms;

public sealed class ExplorerWindowAbsorbedEventArgs : EventArgs
{
    public ExplorerWindowAbsorbedEventArgs(string parsingName, IReadOnlyList<string>? selectedPaths = null)
    {
        ParsingName = parsingName;
        SelectedPaths = selectedPaths ?? Array.Empty<string>();
    }

    /// <summary>被吸收窗口的地址, 可能是 file:/// URL 或 ::{GUID} 形式的 shell 解析名。</summary>
    public string ParsingName { get; }

    /// <summary>
    /// 被吸收窗口在被关闭前选中的项目路径(如“打开文件所在位置”时选中的那个文件)。
    /// 宿主建好标签页后据此恢复选中; 无选中时为空。
    /// </summary>
    public IReadOnlyList<string> SelectedPaths { get; }

    /// <summary>
    /// 宿主是否已成功创建对应标签页。只有为 true 时 watcher 才会关闭原资源管理器窗口,
    /// 从而避免“原窗口已关、标签页没建出来”的情况。
    /// </summary>
    public bool Absorbed { get; set; }
}

/// <summary>
/// 监视系统中新打开的资源管理器窗口, 把它们关闭并通过 <see cref="WindowAbsorbed"/> 抛给宿主转成标签页。
/// 检测: EVENT_OBJECT_CREATE/SHOW 钩子(窗口一创建就隐藏, 之后 explorer 再显示就再隐藏) +
/// EVENT_SYSTEM_FOREGROUND 前台钩子(防抖 + 重试) + 1.5 秒低频轮询兜底。
/// 注意: 外线程隐藏受 WinEvent 投递延迟影响, 只能“尽量减轻”闪烁, 可能残留极短闪。
/// 路径获取: 仍然复用 Shell.Application.Windows() 枚举。
/// </summary>
public sealed class ExplorerWindowWatcher : IDisposable
{
    private static readonly string ExplorerPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    // 控制面板(虚拟文件夹)的固定 CLSID, 用于在吸收前排除控制面板窗口。
    private const string ControlPanelClsid = "26EE0668-A00A-44D7-9371-BEB064C98683";

    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectShow = 0x8002;
    private const uint WineventOutofcontext = 0x0000;
    private const int ObjidWindow = 0;

    private const int SwHide = 0;
    private const int SwShowNoActivate = 8;
    private const string CabinetWindowClass = "CabinetWClass";

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private readonly System.Windows.Forms.Timer _debounceTimer;
    private readonly System.Windows.Forms.Timer _fallbackTimer;
    private readonly HashSet<long> _seen = new();
    private readonly HashSet<long> _hidden = new();
    // 已吸收、正在关闭的窗口 → 记入时刻; 给 Quit 一点时间, 超时仍存在才认为关闭失败并还原。
    private readonly Dictionary<long, long> _closing = new();
    private readonly StringBuilder _classBuffer = new(64);
    private readonly WinEventDelegate _winEventDelegate;
    private readonly WinEventDelegate _shellEventDelegate;

    private IntPtr _winEventHook;
    private IntPtr _createEventHook;
    private dynamic? _shell;
    private int _retriesLeft;
    private bool _disposed;

    public event EventHandler<ExplorerWindowAbsorbedEventArgs>? WindowAbsorbed;

    public ExplorerWindowWatcher()
    {
        // 启动时先记录已经存在的资源管理器窗口, 避免把它们也吸进来。
        Poll(absorbNew: false);

        // 前台切换后延迟 80ms 再枚举; 若还没吸到(窗口尚未进入 Shell.Windows()), 每 50ms 重试, 最多约 1 秒。
        _debounceTimer = new System.Windows.Forms.Timer { Interval = 80 };
        _debounceTimer.Tick += (_, _) =>
        {
            if (_disposed)
            {
                return;
            }

            _debounceTimer.Stop();
            var absorbed = Poll(absorbNew: true);

            if (absorbed == 0 && _retriesLeft-- > 0)
            {
                _debounceTimer.Interval = 50;
                _debounceTimer.Start();
            }
        };

        // 低频兜底: 补上钩子可能漏掉的窗口(后台创建、Explorer 重启等), 并还原“被隐藏后
        // 一直没归入可吸收窗口”的孤儿窗口(如瞬态窗口), 所以间隔取小一些(1.5s)以限制其不可见时长。
        _fallbackTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _fallbackTimer.Tick += (_, _) =>
        {
            if (_disposed)
            {
                return;
            }

            Poll(absorbNew: true, revealOrphans: true);
        };

        _winEventDelegate = OnForegroundChanged;
        _shellEventDelegate = OnShellWindowEvent;
    }

    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        // 先记录“当前已存在”的窗口, 避免关掉再打开吸收开关时把它们一股脑吸成标签页。
        Poll(absorbNew: false);

        _fallbackTimer.Start();

        if (_winEventHook == IntPtr.Zero)
        {
            _winEventHook = SetWinEventHook(
                EventSystemForeground, EventSystemForeground, IntPtr.Zero,
                _winEventDelegate, 0, 0, WineventOutofcontext);
        }

        // 窗口创建/显示都收到通知: 创建时先隐藏, 之后 explorer 若又显示则再隐藏,
        // 从而在它真正绘制前把窗口藏起来。
        if (_createEventHook == IntPtr.Zero)
        {
            _createEventHook = SetWinEventHook(
                EventObjectCreate, EventObjectShow, IntPtr.Zero,
                _shellEventDelegate, 0, 0, WineventOutofcontext);
        }
    }

    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        StopCore();
    }

    private void StopCore()
    {
        _fallbackTimer.Stop();
        _debounceTimer.Stop();

        if (_winEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }

        if (_createEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(_createEventHook);
            _createEventHook = IntPtr.Zero;
        }

        // 停止时把还隐藏着的窗口还原, 避免留下看不见的窗口。
        RevealAll();
        _closing.Clear();
    }

    private void OnForegroundChanged(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        if (_disposed)
        {
            return;
        }

        if (idObject != ObjidWindow || idChild != 0 || hwnd == IntPtr.Zero)
        {
            return;
        }

        // 切换到已存在的资源管理器窗口不需要处理(先做便宜的集合判断)。
        if (_seen.Contains(hwnd.ToInt64()))
        {
            return;
        }

        // 先用便宜的类名过滤, 避免对每次前台切换(任意进程)都做进程查询。
        if (!IsCabinetWindow(hwnd) || !IsExplorerProcess(hwnd))
        {
            return;
        }

        ScheduleAbsorb();
    }

    /// <summary>
    /// 资源管理器窗口“创建/显示”事件: 新窗口一创建就先隐藏, 之后 explorer 若又显示它则立刻再隐藏,
    /// 尽量减轻“一闪而过”, 同时安排吸收。仅针对顶层 CabinetWClass 窗口;
    /// 若最终决定不吸收, 会在 Poll 或停止时还原。
    /// </summary>
    private void OnShellWindowEvent(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        if (_disposed)
        {
            return;
        }

        if (idObject != ObjidWindow || idChild != 0 || hwnd == IntPtr.Zero)
        {
            return;
        }

        var key = hwnd.ToInt64();

        // 已被我们隐藏、正在等待吸收的窗口: explorer 若又想显示它, 立即再隐藏。
        if (_hidden.Contains(key))
        {
            if (eventType == EventObjectShow)
            {
                ShowWindow(hwnd, SwHide);
            }

            return;
        }

        if (eventType != EventObjectCreate || _seen.Contains(key))
        {
            return;
        }

        // 该事件是全系统高频事件, 先用便宜的类名过滤, 再做进程查询。
        if (!IsCabinetWindow(hwnd) || !IsExplorerProcess(hwnd))
        {
            return;
        }

        Hide(hwnd);
        ScheduleAbsorb();
    }

    /// <summary>安排一次防抖吸收, 并允许在窗口尚未进入 Shell.Windows() 时重试。</summary>
    private void ScheduleAbsorb()
    {
        _retriesLeft = 20;
        _debounceTimer.Interval = 80;
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private static bool IsExplorerProcess(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
            {
                return false;
            }

            using var process = Process.GetProcessById((int)pid);
            return string.Equals(process.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 是否为资源管理器文件夹顶层窗口(类名 CabinetWClass)。复用缓冲, 避免高频事件里反复分配。
    /// </summary>
    private bool IsCabinetWindow(IntPtr hwnd)
    {
        _classBuffer.Clear();
        var length = GetClassName(hwnd, _classBuffer, _classBuffer.Capacity);
        return length > 0 && string.Equals(_classBuffer.ToString(), CabinetWindowClass, StringComparison.Ordinal);
    }

    /// <summary>
    /// 隐藏指定窗口, 尽量赶在它绘制前。跨进程无法用 DWM 遮蔽(会 E_ACCESSDENIED),
    /// 所以用 ShowWindow(SW_HIDE); explorer 之后若又显示, 由 OnShellWindowEvent 再次隐藏。
    /// </summary>
    private void Hide(IntPtr hwnd)
    {
        var key = hwnd.ToInt64();
        if (_hidden.Contains(key))
        {
            return;
        }

        ShowWindow(hwnd, SwHide);
        _hidden.Add(key);
    }

    /// <summary>还原被隐藏的窗口。</summary>
    private void Reveal(IntPtr hwnd)
    {
        if (_hidden.Remove(hwnd.ToInt64()))
        {
            ShowWindow(hwnd, SwShowNoActivate);
        }
    }

    /// <summary>
    /// 处理“已吸收、正在关闭”的窗口: 已消失则清掉跟踪; 仍存在(Quit 没生效)则还原并显示,
    /// 避免原窗口被永久隐藏。
    /// </summary>
    private void CleanupClosing()
    {
        if (_closing.Count == 0)
        {
            return;
        }

        const long closeGraceMs = 1000;
        var now = Environment.TickCount64;

        foreach (var pair in _closing.ToArray())
        {
            var key = pair.Key;
            var hwnd = new IntPtr(key);

            if (!IsWindow(hwnd))
            {
                // 已关闭: 清掉跟踪。
                _closing.Remove(key);
                _hidden.Remove(key);
                continue;
            }

            // 仍在: 超过宽限期就认为 Quit 没生效, 还原显示, 避免永久隐藏。
            if (now - pair.Value >= closeGraceMs)
            {
                Reveal(hwnd);
                _closing.Remove(key);
                _hidden.Remove(key);
            }
        }
    }

    /// <summary>还原所有仍被隐藏的窗口(停止监视或退出时调用)。</summary>
    private void RevealAll()
    {
        foreach (var key in new HashSet<long>(_hidden))
        {
            Reveal(new IntPtr(key));
        }
    }

    /// <summary>
    /// 兜底: 还原“被隐藏但始终没有作为可吸收文件夹窗口出现”的窗口, 避免它们一直不可见。
    /// </summary>
    private void RevealOrphans(HashSet<long> visited)
    {
        foreach (var key in new HashSet<long>(_hidden))
        {
            var hwnd = new IntPtr(key);
            if (!IsWindow(hwnd) || !visited.Contains(key))
            {
                Reveal(hwnd);
            }
        }
    }

    private dynamic Shell
    {
        get
        {
            if (_shell == null)
            {
                var type = Type.GetTypeFromProgID("Shell.Application")
                           ?? throw new InvalidOperationException("无法创建 Shell.Application");
                _shell = Activator.CreateInstance(type)!;
            }

            return _shell;
        }
    }

    private int Poll(bool absorbNew, bool revealOrphans = false)
    {
        var absorbedCount = 0;

        dynamic windows;
        try
        {
            windows = Shell.Windows();
        }
        catch
        {
            // Shell.Windows() 失败说明缓存的 Shell.Application 可能已失效, 释放后重建。
            ReleaseShell();
            return 0;
        }

        try
        {
            // 先处理上一轮“已吸收、正在关闭”的窗口: 已消失则清理跟踪; 仍在则还原(Quit 失败)。
            CleanupClosing();

            var current = new HashSet<long>();
            var visited = new HashSet<long>();
            int count;
            try
            {
                count = (int)windows.Count;
            }
            catch
            {
                return 0;
            }

            // 倒序遍历: 吸收成功后 Quit() 会把窗口移出集合, 正序会因下标位移而漏掉窗口。
            for (var i = count - 1; i >= 0; i--)
            {
                dynamic window;
                try
                {
                    window = windows.Item(i);
                }
                catch
                {
                    continue;
                }

                // 每个 window 都是一次 COM 调用产生的 RCW, 用后必须释放, 否则低频轮询会持续累积。
                try
                {
                    long hwnd;
                    if (!IsExplorerWindow(window, out hwnd))
                    {
                        continue;
                    }

                    visited.Add(hwnd);

                    // 解析名(LocationURL / Folder.Self.Path)只取一次, 后面三处都要用。
                    var parsingName = GetParsingName(window);

                    // 控制面板不是真正的文件夹, 不吸收, 保持原窗口不动(若已被隐藏则还原)。
                    if (IsControlPanel(window, parsingName))
                    {
                        Reveal(new IntPtr(hwnd));
                        continue;
                    }

                    // FTP 位置交给系统资源管理器独立浏览, 不吸收(否则原窗口会被关闭并重新
                    // 内嵌到标签页, 而内嵌 ExplorerBrowser 浏览 FTP 会显示空白)。
                    if (IsFtpWindow(parsingName))
                    {
                        Reveal(new IntPtr(hwnd));
                        continue;
                    }

                    current.Add(hwnd);
                    var isNew = _seen.Add(hwnd);

                    if (absorbNew && isNew)
                    {
                        var args = new ExplorerWindowAbsorbedEventArgs(parsingName, GetSelectedPaths(window));

                        // 先让宿主建标签页, 建成功后才关闭原窗口, 保证不会“窗口关了、标签没建出来”。
                        WindowAbsorbed?.Invoke(this, args);
                        if (!args.Absorbed)
                        {
                            // 建标签失败: 还原并保留原窗口。hwnd 已在 _seen 且仍在 current 中,
                            // 后续轮询视为已见, 不会反复重试。
                            Reveal(new IntPtr(hwnd));
                            continue;
                        }

                        try
                        {
                            window.Quit();
                        }
                        catch
                        {
                            // 忽略关闭失败
                        }

                        // 关闭是异步的: 先记入 _closing, 下一轮 Poll 确认窗口已消失再清理跟踪;
                        // 若超过宽限期窗口仍在(Quit 没生效), 由 CleanupClosing 还原, 避免永久隐藏。
                        _closing[hwnd] = Environment.TickCount64;
                        absorbedCount++;
                    }
                }
                finally
                {
                    object? windowObject = window;
                    ReleaseComObject(windowObject);
                }
            }

            // 移除已经消失的窗口句柄, 防止集合无限增长。
            _seen.IntersectWith(current);

            // 兜底轮询时还原“始终没被当成可吸收窗口”的遮蔽窗口, 避免留下看不见的窗口。
            if (revealOrphans)
            {
                RevealOrphans(visited);
            }

            return absorbedCount;
        }
        finally
        {
            object? windowsObject = windows;
            ReleaseComObject(windowsObject);
        }
    }

    private static void ReleaseComObject(object? comObject)
    {
        if (comObject == null)
        {
            return;
        }

        try
        {
            if (Marshal.IsComObject(comObject))
            {
                Marshal.ReleaseComObject(comObject);
            }
        }
        catch
        {
            // 忽略释放失败(如对象已被释放)
        }
    }

    private void ReleaseShell()
    {
        if (_shell == null)
        {
            return;
        }

        object shell = _shell;
        _shell = null;
        ReleaseComObject(shell);
    }

    /// <summary>
    /// 取窗口当前目录的解析名。普通文件夹是 file:/// URL;
    /// 控制面板等非文件系统位置 LocationURL 为空, 改用 Document.Folder.Self.Path(:: {GUID} 形式)。
    /// </summary>
    private static string GetParsingName(dynamic window)
    {
        try
        {
            var url = (string)window.LocationURL;
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }
        }
        catch
        {
            // 忽略, 走下面的兜底
        }

        return GetFolderSelfProperty(window, path: true);
    }

    /// <summary>
    /// 读取 Document.Folder.Self 的 Path/Name。这三级嵌套属性访问各产生一个中间 COM RCW,
    /// 必须显式释放, 否则多次轮询后会累积。
    /// </summary>
    private static string GetFolderSelfProperty(dynamic window, bool path)
    {
        dynamic? document = null;
        dynamic? folder = null;
        dynamic? self = null;

        try
        {
            document = window.Document;
            folder = document.Folder;
            self = folder.Self;
            return (string)(path ? self.Path : self.Name);
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            object? selfObject = self;
            object? folderObject = folder;
            object? documentObject = document;
            ReleaseComObject(selfObject);
            ReleaseComObject(folderObject);
            ReleaseComObject(documentObject);
        }
    }

    /// <summary>
    /// 读取某资源管理器窗口当前选中的项目路径。用于“打开文件所在位置”这类窗口: 吸收后仍能
    /// 选中原文件。Document.SelectedItems() 返回 FolderItems, 逐项取 Path; 虚拟项(Path 为空)
    /// 跳过。中间 COM 对象用后显式释放。
    /// </summary>
    private static IReadOnlyList<string> GetSelectedPaths(dynamic window)
    {
        var result = new List<string>();
        dynamic? items = null;

        try
        {
            items = window.Document.SelectedItems();

            int count;
            try
            {
                count = (int)items.Count;
            }
            catch
            {
                return result;
            }

            for (var i = 0; i < count; i++)
            {
                dynamic? item = null;
                try
                {
                    item = items.Item(i);
                    var path = (string)item.Path;
                    if (!string.IsNullOrEmpty(path))
                    {
                        result.Add(path);
                    }
                }
                catch
                {
                    // 忽略取不到路径的项目
                }
                finally
                {
                    object? itemObject = item;
                    ReleaseComObject(itemObject);
                }
            }
        }
        catch
        {
            // SelectedItems 不可用则视为无选中
        }
        finally
        {
            object? itemsObject = items;
            ReleaseComObject(itemsObject);
        }

        return result;
    }

    /// <summary>
    /// 判断某资源管理器窗口是否是“控制面板”。控制面板是虚拟文件夹,
    /// 解析名 / 文件夹路径固定包含 ControlPanel CLSID, 命中则跳过吸收。
    /// </summary>
    private static bool IsControlPanel(dynamic window, string parsingName)
    {
        // 优先用解析名(LocationURL 或 Document.Folder.Self.Path)里是否含控制面板 CLSID,
        // 该标识与操作系统显示语言无关, 最可靠。
        if (!string.IsNullOrEmpty(parsingName) &&
            parsingName.IndexOf(ControlPanelClsid, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        // 兜底: 某些变体窗口解析名拿不到时, 尝试用文件夹显示名判断。
        var name = GetFolderSelfProperty(window, path: false);
        return string.Equals(name, "Control Panel", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "控制面板", StringComparison.Ordinal);
    }

    /// <summary>
    /// 判断某资源管理器窗口是否是 FTP 位置。FTP 由系统资源管理器独立浏览, 不吸收成标签页。
    /// </summary>
    private static bool IsFtpWindow(string parsingName)
    {
        return parsingName.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExplorerWindow(dynamic window, out long hwnd)
    {
        hwnd = 0;

        try
        {
            var fullName = (string)window.FullName;
            if (!string.Equals(fullName, ExplorerPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            hwnd = Convert.ToInt64(window.HWND);
            return hwnd != 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopCore();
        _debounceTimer.Dispose();
        _fallbackTimer.Dispose();
        ReleaseShell();
    }
}
