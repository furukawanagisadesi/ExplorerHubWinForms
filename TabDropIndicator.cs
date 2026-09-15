namespace ExplorerHubWinForms;

/// <summary>
/// 拖拽标签时显示的“插入位置”竖直指示线。用独立小窗绘制, 可浮在原生 TabControl 之上,
/// 且不可激活、不抢鼠标捕获。
/// </summary>
internal sealed class TabDropIndicator : Form
{
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private const int LineWidth = 2;

    public TabDropIndicator()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = SystemColors.Highlight;
        ClientSize = new Size(LineWidth, 1);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExToolWindow | WsExNoActivate;
            return cp;
        }
    }

    /// <summary>把指示线放到指定的屏幕坐标(顶端), 并设置其高度。</summary>
    public void PlaceAt(Point screenTopLeft, int height)
    {
        Bounds = new Rectangle(screenTopLeft, new Size(LineWidth, Math.Max(height, 1)));
    }
}
