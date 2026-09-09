namespace ExplorerHubWinForms;

public sealed class ExplorerTabControl : TabControl
{
    /// <summary>参数为被双击的标签页索引。</summary>
    public event EventHandler<int>? TabDoubleClicked;

    /// <summary>双击标签区域空白处(非任何标签头)时触发, 用于新建标签页。</summary>
    public event EventHandler? TabAreaDoubleClicked;

    private const int WmLeftButtonDoubleClick = 0x0203;

    /// <summary>
    /// 标签头上的双击会直接投递到本控件(子窗口)的 WndProc, 不会经过父窗体。
    /// 因此这里拦截 WM_LBUTTONDBLCLK 并把坐标交给 HandleTabDoubleClick。
    /// 说明: 标签行的“空白处”双击会投递到 MainForm, 由 MainForm.WndProc 调用 HandleTabDoubleClick。
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmLeftButtonDoubleClick)
        {
            var p = m.LParam.ToInt64();
            HandleTabDoubleClick(new Point((short)(p & 0xFFFF), (short)((p >> 16) & 0xFFFF)));
            return;
        }

        base.WndProc(ref m);
    }

    /// <summary>
    /// 对双击点做命中判定(坐标为本控件客户区)。命中标签头 → 关闭; 否则若在标签行内 → 新建。
    /// </summary>
    public void HandleTabDoubleClick(Point clientPoint)
    {
        for (var i = 0; i < TabCount; i++)
        {
            if (GetTabRect(i).Contains(clientPoint))
            {
                TabDoubleClicked?.Invoke(this, i);
                return;
            }
        }

        if (IsInTabStrip(clientPoint))
        {
            TabAreaDoubleClicked?.Invoke(this, EventArgs.Empty);
            return;
        }
    }

    /// <summary>
    /// 判断点是否落在标签行(标签头区域)内。内容区 DisplayRectangle 从标签头之下开始,
    /// 所以 y 在 标签行顶部 与 内容区上边缘 之间即处于标签行。
    /// </summary>
    private bool IsInTabStrip(Point pt)
    {
        if (TabCount == 0)
        {
            return false;
        }

        var contentTop = DisplayRectangle.Top;
        var tabTop = GetTabRect(TabCount - 1).Top;

        return pt.Y >= tabTop && pt.Y < contentTop;
    }
}
