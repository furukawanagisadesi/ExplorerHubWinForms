using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.WindowsAPICodePack.Controls.WindowsForms;

namespace ExplorerHubWinForms;

/// <summary>
/// 让内嵌 ExplorerBrowser 选中指定路径的项目。WindowsAPICodePack 没有公开的选中 API;
/// 这里沿用库内部的做法: 反射取私有字段 ExplorerBrowserControl(IExplorerBrowser), 调用其
/// 公开的 GetCurrentView 拿到当前 IShellView 与 IFolderView, 再用当前文件夹的
/// IShellFolder.ParseDisplayName 把文件名转成“文件夹相对”的子 PIDL, 最后
/// IShellView.SelectItem 选中。
/// 注意: IShellView.SelectItem 需要的是相对子 PIDL, 传绝对 PIDL 会返回 E_INVALIDARG。
/// </summary>
internal static class ShellItemSelector
{
    private static readonly Guid IidShellView = new("000214E3-0000-0000-C000-000000000046");
    private static readonly Guid IidFolderView = new("cde725b0-ccc9-4519-917e-325d72fab4ce");
    private static readonly Guid IidShellFolder = new("000214E6-0000-0000-C000-000000000046");

    private const uint SvsiSelect = 0x00000001;
    private const uint SvsiDeselectOthers = 0x00000004;
    private const uint SvsiEnsureVisible = 0x00000008;
    private const uint SvsiFocused = 0x00000010;

    /// <summary>
    /// 当前视图所在文件夹是否已是给定路径的父文件夹。用于在“导航到位”后才恢复选中,
    /// 避免 ExplorerBrowser 初始化时的桌面视图误触发。
    /// </summary>
    public static bool IsAtParentFolder(ExplorerBrowser browser, IReadOnlyList<string> paths)
    {
        var current = GetCurrentFolderPath(browser);
        if (current == null)
        {
            return false;
        }

        foreach (var path in paths)
        {
            var parent = GetParentPath(path);
            if (parent != null && PathsEqual(current, parent))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>尝试选中给定路径的项目; 视图取不到、路径不在当前文件夹或无效时静默失败。</summary>
    public static bool TrySelect(ExplorerBrowser browser, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return false;
        }

        var current = GetCurrentFolderPath(browser);
        if (current == null)
        {
            return false;
        }

        var viewObject = GetCurrentView(browser, IidShellView);
        var folderViewObject = GetCurrentView(browser, IidFolderView);
        if (viewObject is not IShellView shellView || folderViewObject is not IFolderView folderView)
        {
            ReleaseComObject(viewObject);
            ReleaseComObject(folderViewObject);
            return false;
        }

        try
        {
            var shellFolderIid = IidShellFolder;
            if (folderView.GetFolder(ref shellFolderIid, out var folderPtr) != 0 || folderPtr == IntPtr.Zero)
            {
                return false;
            }

            var shellFolder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
            try
            {
                var selectedAny = false;
                foreach (var path in paths)
                {
                    var parent = GetParentPath(path);
                    var fileName = Path.GetFileName(path);
                    if (parent == null || fileName.Length == 0 || !PathsEqual(current, parent))
                    {
                        continue;
                    }

                    var attributes = 0u;
                    if (shellFolder.ParseDisplayName(
                            IntPtr.Zero, IntPtr.Zero, fileName, out _, out var childPidl, ref attributes) != 0
                        || childPidl == IntPtr.Zero)
                    {
                        continue;
                    }

                    try
                    {
                        var flags = SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible | SvsiFocused;
                        if (shellView.SelectItem(childPidl, flags) == 0)
                        {
                            selectedAny = true;
                        }
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(childPidl);
                    }
                }

                return selectedAny;
            }
            finally
            {
                Marshal.ReleaseComObject(shellFolder);
            }
        }
        catch
        {
            // 视图/项目异常一律忽略, 不影响标签页正常使用。
            return false;
        }
        finally
        {
            Marshal.ReleaseComObject(shellView);
            Marshal.ReleaseComObject(folderView);
        }
    }

    private static object? GetCurrentView(ExplorerBrowser browser, Guid iid)
    {
        try
        {
            var field = typeof(ExplorerBrowser).GetField(
                "ExplorerBrowserControl", BindingFlags.NonPublic | BindingFlags.Instance);
            var control = field?.GetValue(browser);
            if (control == null)
            {
                return null;
            }

            var method = control.GetType().GetMethod(
                "GetCurrentView", BindingFlags.Public | BindingFlags.Instance);
            if (method == null)
            {
                return null;
            }

            var args = new object[] { iid, IntPtr.Zero };
            if (Convert.ToInt32(method.Invoke(control, args)) != 0)
            {
                return null;
            }

            var viewPtr = (IntPtr)args[1];
            return viewPtr == IntPtr.Zero ? null : Marshal.GetObjectForIUnknown(viewPtr);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetCurrentFolderPath(ExplorerBrowser browser)
    {
        try
        {
            var location = browser.NavigationLog.CurrentLocation;
            return location is { IsFileSystemObject: true } ? location.ParsingName : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetParentPath(string path)
    {
        try
        {
            return Path.GetDirectoryName(path);
        }
        catch
        {
            return null;
        }
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

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
            // 忽略释放失败
        }
    }

    /// <summary>
    /// IShellView 的最小声明: 只需保证 SelectItem 位于正确 vtable 槽位, 前面的方法仅用于占位。
    /// </summary>
    [ComImport]
    [Guid("000214E3-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        [PreserveSig] int GetWindow(out IntPtr phwnd);
        [PreserveSig] int ContextSensitiveHelp(bool fEnterMode);
        [PreserveSig] int TranslateAccelerator(IntPtr pmsg);
        [PreserveSig] int EnableModeless(bool fEnable);
        [PreserveSig] int UIActivate(uint uState);
        [PreserveSig] int Refresh();
        [PreserveSig] int CreateViewWindow(
            [MarshalAs(UnmanagedType.IUnknown)] object psvPrevious, IntPtr pfs,
            [MarshalAs(UnmanagedType.IUnknown)] object psb, IntPtr prcView, out IntPtr phWnd);
        [PreserveSig] int DestroyViewWindow();
        [PreserveSig] int GetCurrentInfo(out IntPtr pfs);
        [PreserveSig] int AddPropertySheetPages(uint dwReserved, IntPtr pfn, uint lparam);
        [PreserveSig] int SaveViewState();
        [PreserveSig] int SelectItem(IntPtr pidlItem, uint uFlags);
        [PreserveSig] int GetItemObject(int uItem, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);
    }

    /// <summary>IFolderView 只需用到 GetFolder, 前面的方法用于占位。</summary>
    [ComImport]
    [Guid("cde725b0-ccc9-4519-917e-325d72fab4ce")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        [PreserveSig] int GetCurrentViewMode(out uint pViewMode);
        [PreserveSig] int SetCurrentViewMode(uint viewMode);
        [PreserveSig] int GetFolder(ref Guid riid, out IntPtr ppv);
    }

    /// <summary>IShellFolder 只需用到 ParseDisplayName。</summary>
    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(
            IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
    }
}
