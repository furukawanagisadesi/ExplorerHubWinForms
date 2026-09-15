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
    private Point _grabOffset;
    private bool _dragging;
    private int _dropIndex = -1;
    private TabDragGhost? _ghost;
    private TabDropIndicator? _indicator;

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
    /// 拖拽标签头: 超过阈值后出现跟随鼠标的半透明“影子”(类似桌面拖图标), 标签本身不动,
    /// 另用竖直指示线标出松手后的落点; 松开鼠标才真正换位。
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

                // 超过阈值才判定为拖拽, 避免单击/双击被误判。
                BeginDrag();
            }

            UpdateDrag(e.Location);
        }

        base.OnMouseMove(e);
    }

    /// <summary>超过拖拽阈值后启动拖拽: 造影子与指示线, 并捕获鼠标。</summary>
    private void BeginDrag()
    {
        _dragging = true;

        var rect = GetTabRect(_dragIndex);
        _grabOffset = new Point(_dragStart.X - rect.Left, _dragStart.Y - rect.Top);

        var owner = FindForm();

        _ghost = new TabDragGhost(CreateTabImage(_dragIndex)) { Owner = owner };
        _ghost.Show();

        _indicator = new TabDropIndicator { Owner = owner };
        _indicator.Show();

        // 放在显示影子之后: 万一显示影子导致鼠标捕获变化, 这里再抢回来。
        Capture = true;
    }

    /// <summary>让影子跟随鼠标, 并按当前落点更新竖直指示线。</summary>
    private void UpdateDrag(Point cursor)
    {
        if (_ghost != null)
        {
            _ghost.Location = new Point(
                Cursor.Position.X - _grabOffset.X,
                Cursor.Position.Y - _grabOffset.Y);
        }

        _dropIndex = ComputeDropIndex(cursor);
        UpdateIndicator();
    }

    /// <summary>松开鼠标: 只在此刻换位一次, 然后清理影子与指示线。</summary>
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging)
        {
            var from = _dragIndex;
            var to = ComputeDropIndex(e.Location);
            EndDrag();

            if (from >= 0 && from < TabCount && to != from)
            {
                MoveTab(from, to);
            }
        }
        else
        {
            _dragIndex = -1;
        }

        base.OnMouseUp(e);
    }

    /// <summary>拖拽中若鼠标捕获被别处夺走(如 Alt+Tab), 取消本次拖拽并清理。</summary>
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        if (_dragging && !Capture)
        {
            EndDrag();
        }

        base.OnMouseCaptureChanged(e);
    }

    /// <summary>结束拖拽并释放影子、指示线与鼠标捕获。</summary>
    private void EndDrag()
    {
        _dragging = false;
        _dragIndex = -1;
        _dropIndex = -1;

        if (Capture)
        {
            Capture = false;
        }

        _ghost?.Dispose();
        _ghost = null;
        _indicator?.Dispose();
        _indicator = null;
    }

    /// <summary>
    /// 计算被拖标签的落点索引: 结果是“去掉被拖标签后”的插入位置, 可直接交给 MoveTab。
    /// 拖拽期间标签布局不变, 所以该索引稳定, 不会像实时换位那样来回跳。
    /// </summary>
    private int ComputeDropIndex(Point cursor)
    {
        var position = 0;
        for (var i = 0; i < TabCount; i++)
        {
            if (i == _dragIndex)
            {
                continue;
            }

            var rect = GetTabRect(i);
            if (cursor.X < rect.Left + rect.Width / 2)
            {
                return position;
            }

            position++;
        }

        return position;
    }

    /// <summary>按当前落点把竖直指示线画到对应的标签边界上。</summary>
    private void UpdateIndicator()
    {
        if (_indicator == null)
        {
            return;
        }

        var others = OtherTabIndices();
        if (others.Count == 0)
        {
            return;
        }

        var anchor = others[Math.Min(_dropIndex, others.Count - 1)];
        var anchorRect = GetTabRect(anchor);
        var boundaryX = _dropIndex < others.Count ? anchorRect.Left : anchorRect.Right;

        _indicator.PlaceAt(PointToScreen(new Point(boundaryX, anchorRect.Top)), anchorRect.Height);
    }

    /// <summary>除被拖标签外的所有标签索引, 保持原顺序。</summary>
    private List<int> OtherTabIndices()
    {
        var list = new List<int>(TabCount);
        for (var i = 0; i < TabCount; i++)
        {
            if (i != _dragIndex)
            {
                list.Add(i);
            }
        }

        return list;
    }

    /// <summary>把某个标签头画成一张位图, 作为拖拽影子的内容。</summary>
    private Bitmap CreateTabImage(int index)
    {
        var rect = GetTabRect(index);
        var width = Math.Max(rect.Width, 1);
        var height = Math.Max(rect.Height, 1);

        var bitmap = new Bitmap(width, height);
        using var g = Graphics.FromImage(bitmap);
        g.FillRectangle(SystemBrushes.ControlLightLight, 0, 0, width, height);
        g.DrawRectangle(SystemPens.ControlDark, 0, 0, width - 1, height - 1);
        TextRenderer.DrawText(
            g,
            TabPages[index].Text,
            Font,
            new Rectangle(2, 0, Math.Max(width - 4, 1), height),
            SystemColors.ControlText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        return bitmap;
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // 拖拽中被销毁(如程序退出)时, 清掉可能还显示的影子与指示线。
            _ghost?.Dispose();
            _ghost = null;
            _indicator?.Dispose();
            _indicator = null;
        }

        base.Dispose(disposing);
    }
}
