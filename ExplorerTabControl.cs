namespace ExplorerHubWinForms;

public sealed class ExplorerTabControl : TabControl
{
    /// <summary>参数为被双击的标签页索引。</summary>
    public event EventHandler<int>? TabDoubleClicked;

    /// <summary>双击标签区域空白处(非任何标签头)时触发, 用于新建标签页。</summary>
    public event EventHandler? TabAreaDoubleClicked;

    private const int WmLeftButtonDoubleClick = 0x0203;

    private int _dragIndex = -1;
    private Point _dragStart;
    private bool _dragging;

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

    /// <summary>
    /// 按下左键时记住光标所在的标签头。真正的换位要等移动超过拖拽阈值后在 OnMouseMove 里做,
    /// 这样单击与双击不会被误判成拖拽。
    /// </summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        _dragIndex = e.Button == MouseButtons.Left ? TabIndexAt(e.Location) : -1;
        _dragStart = e.Location;
        _dragging = false;

        base.OnMouseDown(e);
    }

    /// <summary>
    /// 拖拽标签头时实时换位: 光标进入另一个标签矩形就把被拖标签移过去, 产生跟手的换位效果。
    /// </summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragIndex >= 0 && (e.Button & MouseButtons.Left) != 0)
        {
            if (!_dragging)
            {
                var dragSize = SystemInformation.DragSize;
                if (Math.Abs(e.X - _dragStart.X) < dragSize.Width &&
                    Math.Abs(e.Y - _dragStart.Y) < dragSize.Height)
                {
                    base.OnMouseMove(e);
                    return;
                }

                // 超过阈值才判定为拖拽, 并捕获鼠标, 避免快速拖动时消息丢给别的控件。
                _dragging = true;
                Capture = true;
            }

            var target = TabIndexAt(e.Location);
            if (target >= 0 && target != _dragIndex)
            {
                MoveTab(_dragIndex, target);
                _dragIndex = target;
            }
        }

        base.OnMouseMove(e);
    }

    /// <summary>松开鼠标结束拖拽, 并释放捕获。</summary>
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging)
        {
            Capture = false;
        }

        _dragIndex = -1;
        _dragging = false;

        base.OnMouseUp(e);
    }

    /// <summary>命中光标所在的标签头索引; 不在任何标签头上返回 -1。</summary>
    private int TabIndexAt(Point pt)
    {
        for (var i = 0; i < TabCount; i++)
        {
            if (GetTabRect(i).Contains(pt))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 把标签页从 from 移到 to。TabControl 没有公开的移动方法, 只能先移除再插入;
    /// TabPage 移除时不销毁句柄, 重新插入是重挂载, 内嵌的 ExplorerBrowser 状态得以保留。
    /// 换位后让被拖标签保持选中。
    /// </summary>
    private void MoveTab(int from, int to)
    {
        var page = TabPages[from];
        TabPages.RemoveAt(from);
        TabPages.Insert(to, page);
        SelectedTab = page;
    }
}
