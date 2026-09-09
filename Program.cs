namespace ExplorerHubWinForms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // 兜底: 捕获未处理异常并提示, 而不是直接崩溃退出。
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowError(e.ExceptionObject as Exception);

        Application.Run(new MainForm());
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
