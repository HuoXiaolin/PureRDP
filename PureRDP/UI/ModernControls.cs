using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PureRDP.UI;

internal class BufferedPanel : Panel
{
    public BufferedPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
    }
}

internal sealed class BufferedFlowLayoutPanel : FlowLayoutPanel
{
    public BufferedFlowLayoutPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
    }
}

internal enum ModernButtonKind
{
    Primary,
    Secondary,
    Ghost,
}

internal sealed class ModernButton : Button
{
    private bool _hovered;
    private bool _pressed;

    [DefaultValue(ModernButtonKind.Secondary)]
    public ModernButtonKind Kind { get; set; } = ModernButtonKind.Secondary;
    [DefaultValue(false)]
    public bool Selected { get; set; }
    [DefaultValue(true)]
    public bool Bordered { get; set; } = true;

    public ModernButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
        TabStop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        using var brush = new SolidBrush(SurfaceColor());
        pevent.Graphics.FillRectangle(brush, pevent.ClipRectangle);
    }

    private Color SurfaceColor()
    {
        for (Control? surface = Parent; surface != null; surface = surface.Parent)
            if (surface.BackColor.A == 255)
                return surface.BackColor;
        return UiDesignTokens.Surface;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Every paint owns a complete opaque frame, including rounded corners.
        using (var background = new SolidBrush(SurfaceColor()))
            e.Graphics.FillRectangle(background, ClientRectangle);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
        using var path = RoundedPath(bounds, Math.Min(UiDesignTokens.ButtonRadius, Height / 3));

        var fill = Kind switch
        {
            ModernButtonKind.Primary => _pressed ? UiDesignTokens.AccentPressed :
                _hovered ? UiDesignTokens.AccentHover : UiDesignTokens.Accent,
            ModernButtonKind.Ghost => _hovered ? UiDesignTokens.DangerSoft : Color.Transparent,
            _ => Selected ? UiDesignTokens.AccentSoft : _pressed ? UiDesignTokens.SurfacePressed :
                _hovered ? UiDesignTokens.AccentSoft : Color.Transparent,
        };
        if (!Enabled) fill = UiDesignTokens.SurfaceSoft;
        using (var brush = new SolidBrush(fill))
            e.Graphics.FillPath(brush, path);
        if (Kind == ModernButtonKind.Secondary && !Selected && Bordered)
        {
            using var border = new Pen(UiDesignTokens.BorderStrong);
            e.Graphics.DrawPath(border, path);
        }

        var textColor = Kind switch
        {
            ModernButtonKind.Primary => Color.White,
            ModernButtonKind.Ghost => _hovered ? UiDesignTokens.Danger : UiDesignTokens.TextMuted,
            _ => Selected ? UiDesignTokens.Accent : UiDesignTokens.TextSecondary,
        };
        if (!Enabled) textColor = UiDesignTokens.TextMuted;
        TextRenderer.DrawText(e.Graphics, Text, Font, bounds, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
            TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -4, -4),
                textColor, fill);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return path;

        var diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), Math.Max(2, radius * 2));
        if (diameter <= 1)
        {
            path.AddRectangle(bounds);
            return path;
        }
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class SkyHeroPanel : Panel
{
    public SkyHeroPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var bounds = ClientRectangle;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var gradient = new LinearGradientBrush(bounds,
            UiDesignTokens.HeroSky, UiDesignTokens.HeroGrass, LinearGradientMode.Vertical);
        e.Graphics.FillRectangle(gradient, bounds);
        using var cloud = new SolidBrush(Color.FromArgb(92, 255, 255, 255));
        e.Graphics.FillEllipse(cloud, bounds.Width - 230, 18, 118, 34);
        e.Graphics.FillEllipse(cloud, bounds.Width - 168, 4, 96, 46);
        e.Graphics.FillEllipse(cloud, bounds.Width - 92, 22, 78, 30);
    }
}

internal sealed class CircularPanel : Panel
{
    public CircularPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? UiDesignTokens.PageBackground);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        e.Graphics.CompositingQuality = CompositingQuality.HighQuality;

        if (Width <= 2 || Height <= 2)
            return;

        // Keep the avatar circular without relying on a bitmap or a hard
        // control Region. The latter leaves visibly jagged edges at common
        // Windows scaling factors and also clips the child label abruptly.
        var diameter = Math.Max(1F, Math.Min(Width, Height) - 4F);
        var left = (Width - diameter) / 2F;
        var top = (Height - diameter) / 2F;
        using (var shadow = new SolidBrush(Color.FromArgb(28, 22, 54, 68)))
            e.Graphics.FillEllipse(shadow, left, top + 1.25F, diameter, diameter);
        using (var brush = new SolidBrush(BackColor))
            e.Graphics.FillEllipse(brush, left, top, diameter, diameter);

        // A quiet translucent rim separates adjacent accent colours while
        // keeping the avatar flat and modern.
        using var rim = new Pen(Color.FromArgb(92, 255, 255, 255), 1F);
        e.Graphics.DrawEllipse(rim, left + 0.5F, top + 0.5F, diameter - 1F, diameter - 1F);
    }
}

internal sealed class RoundedSurfacePanel : Panel
{
    public RoundedSurfacePanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = UiDesignTokens.Surface;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? UiDesignTokens.PageBackground);
        var bounds = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
        using var path = RoundedPath(bounds, Math.Min(12, Height / 3));
        using var brush = new SolidBrush(BackColor);
        using var pen = new Pen(UiDesignTokens.Border, 1);
        e.Graphics.FillPath(brush, path);
        e.Graphics.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return path;

        var diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), Math.Max(2, radius * 2));
        if (diameter <= 1)
        {
            path.AddRectangle(bounds);
            return path;
        }
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
