using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ExplorerHubWinForms;

public sealed class ExplorerWindowAbsorbedEventArgs : EventArgs
{
    public ExplorerWindowAbsorbedEventArgs(string parsingName)
    {
        ParsingName = parsingName;
    }

    /// <summary>被吸收窗口的地址, 可能是 file:/// URL 或 ::{GUID} 形式的 shell 解析名。</summary>
    public string ParsingName { get; }
}

/// <summary>
/// 监视系统中新打开的资源管理器窗口, 把它们关闭并通过 <see cref="WindowAbsorbed"/> 抛给宿主转成标签页。
/// 检测: EVENT_SYSTEM_FOREGROUND 前台钩子即时触发(防抖 + 重试) + 3 秒低频轮询兜底。
/// 路径获取: 仍然复用 Shell.Application.Windows() 枚举。
/// </summary>
public sealed class ExplorerWindowWatcher : IDisposable
{
    private static readonly string ExplorerPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    // 控制面板(虚拟文件夹)的固定 CLSID, 用于在吸收前排除控制面板窗口。
    private const string ControlPanelClsid = "26EE0668-A00A-44D7-9371-BEB064C98683";

    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutofcontext = 0x0000;
    private const int ObjidWindow = 0;

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

    private readonly System.Windows.Forms.Timer _debounceTimer;
    private readonly System.Windows.Forms.Timer _fallbackTimer;
    private readonly HashSet<long> _seen = new();
    private readonly WinEventDelegate _winEventDelegate;

    private IntPtr _winEventHook;
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

        // 低频兜底: 补上钩子可能漏掉的窗口(后台创建、Explorer 重启等)。
        _fallbackTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _fallbackTimer.Tick += (_, _) =>
        {
            if (_disposed)
            {
                return;
            }

            Poll(absorbNew: true);
        };

        _winEventDelegate = OnForegroundChanged;
    }

    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        _fallbackTimer.Start();

        if (_winEventHook == IntPtr.Zero)
        {
            _winEventHook = SetWinEventHook(
                EventSystemForeground, EventSystemForeground, IntPtr.Zero,
                _winEventDelegate, 0, 0, WineventOutofcontext);
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

        if (!IsExplorerProcess(hwnd))
        {
            return;
        }

        // 切换到已存在的资源管理器窗口不需要处理。
        if (_seen.Contains(hwnd.ToInt64()))
        {
            return;
        }

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

    private int Poll(bool absorbNew)
    {
        var absorbedCount = 0;

        dynamic windows;
        try
        {
            windows = Shell.Windows();
        }
        catch
        {
            _shell = null;
            return 0;
        }

        var current = new HashSet<long>();
        int count;
        try
        {
            count = (int)windows.Count;
        }
        catch
        {
            return 0;
        }

        for (var i = 0; i < count; i++)
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

            long hwnd;
            if (!IsExplorerWindow(window, out hwnd))
            {
                continue;
            }

            // 控制面板不是真正的文件夹, 不吸收, 保持原窗口不动。
            if (IsControlPanel(window))
            {
                continue;
            }

            current.Add(hwnd);
            var isNew = _seen.Add(hwnd);

            if (absorbNew && isNew)
            {
                var parsingName = GetParsingName(window);

                try
                {
                    window.Quit();
                }
                catch
                {
                    // 忽略关闭失败
                }

                absorbedCount++;
                WindowAbsorbed?.Invoke(this, new ExplorerWindowAbsorbedEventArgs(parsingName));
            }
        }

        // 移除已经消失的窗口句柄, 防止集合无限增长。
        _seen.IntersectWith(current);
        return absorbedCount;
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

        try
        {
            return (string)window.Document.Folder.Self.Path;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 判断某资源管理器窗口是否是“控制面板”。控制面板是虚拟文件夹,
    /// 解析名 / 文件夹路径固定包含 ControlPanel CLSID, 命中则跳过吸收。
    /// </summary>
    private static bool IsControlPanel(dynamic window)
    {
        // 优先用解析名(LocationURL 或 Document.Folder.Self.Path)里是否含控制面板 CLSID,
        // 该标识与操作系统显示语言无关, 最可靠。
        var parsingName = GetParsingName(window);
        if (!string.IsNullOrEmpty(parsingName) &&
            parsingName.IndexOf(ControlPanelClsid, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        // 兜底: 某些变体窗口解析名拿不到时, 尝试用文件夹显示名判断。
        try
        {
            var name = (string)window.Document.Folder.Self.Name;
            return string.Equals(name, "Control Panel", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(name, "控制面板", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
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

        if (_shell != null)
        {
            try
            {
                Marshal.ReleaseComObject(_shell);
            }
            catch
            {
                // 忽略
            }

            _shell = null;
        }
    }
}
