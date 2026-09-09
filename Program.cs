using System.Runtime.InteropServices;

namespace ExplorerHubWinForms;

internal static class Program
{
    // 单实例互斥锁名: 用普通名(会话级)而非 Global\, 避免不同登录会话互相干扰。
    private const string MutexName = "ExplorerHubWinForms_SingleInstance";

    // 自定义窗口消息: 已运行的实例收到后显示并激活主窗口。
    private const string ShowMainWindowMessageName = "ExplorerHub_ShowMainWindow";
    internal static readonly uint ShowMainWindowMessageId = RegisterWindowMessage(ShowMainWindowMessageName);

    // HWND_BROADCAST: 向所有顶层窗口广播。
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);

    private static Mutex? _singleInstanceMutex;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // 单实例: 拿不到互斥锁说明已有实例在运行, 通知它显示主窗口后退出。
        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            SignalExistingInstance();
            return;
        }

        // 兜底: 捕获未处理异常并提示, 而不是直接崩溃退出。
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowError(e.ExceptionObject as Exception);

        Application.Run(new MainForm());
    }

    /// <summary>向已在运行的实例广播“显示主窗口”消息。</summary>
    private static void SignalExistingInstance()
    {
        if (ShowMainWindowMessageId != 0)
        {
            PostMessage(HwndBroadcast, ShowMainWindowMessageId, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static void ShowError(Exception? exception)
    {
        // "Browsing to object failed" 是 ExplorerBrowser 无法浏览某些 shell 对象
        // (如控制面板里的 .cpl 小程序)导致的, 它们本就不是文件夹, 直接忽略即可。
        if (exception is Microsoft.WindowsAPICodePack.Controls.CommonControlException)
        {
            return;
        }

        MessageBox.Show(exception?.ToString() ?? "发生未知错误。", "ExplorerHub - 错误",
            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
