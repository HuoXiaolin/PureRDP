using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using PureRDP.Configuration.Enums;
namespace PureRDP.UI;

/// <summary>Static settings illustration; never creates or operates a remote session.</summary>
internal sealed class ConnectionChromePreview(Func<ControlBarPosition> position, Func<bool> rounded) : Control
{
    protected override Size DefaultSize => new(300, 96);
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(2, 2, Width - 5, Height - 5);
        using var fill = new SolidBrush(UiDesignTokens.SurfaceSubtle);
        using var edge = new Pen(UiDesignTokens.BorderStrong);
        if (rounded()) {
            using var path = SettingsShape.Rounded(r, 10);
            g.FillPath(fill, path); g.DrawPath(edge, path);
        } else { g.FillRectangle(fill, r); g.DrawRectangle(edge, r.X, r.Y, r.Width, r.Height); }
        var horizontal = position() is ControlBarPosition.Top or ControlBarPosition.Bottom;
        var rail = horizontal ? new Rectangle((Width - 44) / 2, position() == ControlBarPosition.Top ? 7 : Height - 13, 44, 6)
            : new Rectangle(position() == ControlBarPosition.Left ? 7 : Width - 13, (Height - 44) / 2, 6, 44);
        using var railFill = new SolidBrush(Color.FromArgb(116, 162, 175));
        using var railPath = SettingsShape.Rounded(rail, 3);
        g.FillPath(railFill, railPath);
        TextRenderer.DrawText(g, "控制栏位置示意", Font, new Rectangle(18, 26, Width - 36, Height - 52),
            UiDesignTokens.TextSecondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
