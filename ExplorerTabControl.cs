namespace ExplorerHubWinForms;

/// <summary>
/// 支持双击标签关闭的 TabControl。
/// TabControl 会把标签头上的双击事件内部消化, 因此这里重写 WndProc 直接处理 WM_LBUTTONDBLCLK。
/// </summary>
public sealed class ExplorerTabControl : TabControl
{
    private const int WmLeftButtonDoubleClick = 0x0203;

    /// <summary>参数为被双击的标签页索引。</summary>
    public event EventHandler<int>? TabDoubleClicked;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmLeftButtonDoubleClick)
        {
            var lParam = m.LParam.ToInt64();
            var x = (short)(lParam & 0xFFFF);
            var y = (short)((lParam >> 16) & 0xFFFF);

            for (var i = 0; i < TabCount; i++)
            {
                if (GetTabRect(i).Contains(x, y))
                {
                    TabDoubleClicked?.Invoke(this, i);
                    return;
                }
            }
        }

        base.WndProc(ref m);
    }
}
