using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PureRDP.UI;

internal sealed class FavoriteStarButton : Button
{
    private bool _pinned;
    private bool _hovered;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool IsPinned
    {
        get => _pinned;
        set
        {
            _pinned = value;
            AccessibleName = value ? "取消置顶" : "置顶设备";
            AccessibleDescription = value ? "此设备已置顶；点击取消置顶。" : "点击后将设备置顶。";
            Invalidate();
        }
    }
    internal FavoriteStarButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        BackColor = UiDesignTokens.Surface;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.CheckButton;
        IsPinned = false;
        TabStop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(BackColor);
        var size = Math.Min(Width, Height);
        var x = (Width - size) / 2f;
        var y = (Height - size) / 2f;
        if (_hovered || Focused)
        {
            using var hover = new SolidBrush(Color.FromArgb(255, 248, 224));
            graphics.FillEllipse(hover, x + 1, y + 1, size - 2, size - 2);
        }
        // A small vector star, centered inside a larger comfortable click target.
        var points = new PointF[10];
        for (var index = 0; index < points.Length; index++)
        {
            var angle = -Math.PI / 2 + index * Math.PI / 5;
            var radius = size * (index % 2 == 0 ? .30f : .135f);
            points[index] = new PointF(x + size / 2f + (float)Math.Cos(angle) * radius,
                y + size / 2f + (float)Math.Sin(angle) * radius);
        }
        using var star = new GraphicsPath();
        star.AddPolygon(points);
        if (_pinned)
        {
            using var fill = new SolidBrush(Color.FromArgb(246, 196, 69));
            graphics.FillPath(fill, star);
        }
        var color = _pinned ? Color.FromArgb(168, 117, 8) : UiDesignTokens.TextMuted;
        using var outline = new Pen(color, Math.Max(1.1f, size / 24f)) { LineJoin = LineJoin.Round };
        graphics.DrawPath(outline, star);
        if (Focused) ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(ClientRectangle, -2, -2));
    }
}
