namespace ExplorerHubWinForms;

/// <summary>
/// 拖拽标签时跟随鼠标的半透明“影子”窗口, 效果类似桌面拖动图标。
/// 无边框、不进任务栏/Alt-Tab、不可激活, 不抢鼠标捕获。
/// </summary>
internal sealed class TabDragGhost : Form
{
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private readonly Bitmap _image;

    public TabDragGhost(Bitmap image)
    {
        _image = image;
        // 影子按设备像素与标签矩形对齐, 关掉自动缩放避免高 DPI 下变大。
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ClientSize = image.Size;
        Opacity = 0.7;
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

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.DrawImageUnscaled(_image, 0, 0);
        base.OnPaint(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _image.Dispose();
        }

        base.Dispose(disposing);
    }
}
