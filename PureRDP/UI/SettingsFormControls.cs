using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PureRDP.UI;

internal static class SettingsShape
{
    internal static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class SettingsGroupBox : GroupBox
{
    public SettingsGroupBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (SystemInformation.HighContrast) { base.OnPaint(e); return; }
        if (Width < 4 || Height < 4) return;
        e.Graphics.Clear(Parent?.BackColor ?? UiDesignTokens.PageBackground);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = SettingsShape.Rounded(new RectangleF(1, 1, Width - 3, Height - 3), 10 * DeviceDpi / 96F);
        using var fill = new SolidBrush(BackColor);
        using var border = new Pen(UiDesignTokens.Border);
        e.Graphics.FillPath(fill, shape);
        e.Graphics.DrawPath(border, shape);
        var inset = (int)Math.Round(16 * DeviceDpi / 96F);
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(inset, inset / 2,
            Math.Max(1, Width - inset * 2), Font.Height + 6), UiDesignTokens.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }
}

// The original TextBox remains responsible for editing, caret, password
// masking and accessibility. This wrapper supplies the shared field outline.
internal sealed class SettingsTextField : BufferedPanel
{
    internal TextBox Editor { get; }
    public SettingsTextField(TextBox editor)
    {
        Editor = editor;
        TabIndex = editor.TabIndex;
        BackColor = UiDesignTokens.SurfaceSubtle;
        editor.BorderStyle = BorderStyle.None;
        editor.BackColor = BackColor;
        editor.AutoSize = false;
        editor.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        Controls.Add(editor);
        editor.GotFocus += (_, _) => Invalidate();
        editor.LostFocus += (_, _) => Invalidate();
        Click += (_, _) => editor.Focus();
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Editor == null) return;
        var inset = (int)Math.Round(8 * DeviceDpi / 96F);
        var height = Math.Min(Math.Max(1, Height - 4), Editor.Font.Height + 2);
        Editor.SetBounds(inset, Math.Max(2, (Height - height) / 2), Math.Max(1, Width - inset * 2), height);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < 2 || Height < 2) return;
        using var border = new Pen(Editor.Focused ? UiDesignTokens.Accent : UiDesignTokens.BorderStrong);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }
}

// A CheckBox subclass retains native checking, keyboard navigation and bound
// events; only the green theme glyph and text are drawn here.
internal sealed class SettingsCheckBox : CheckBox
{
    private bool _hovered;
    public SettingsCheckBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    private int GlyphSize => Math.Max(14, (int)Math.Round(14 * DeviceDpi / 96F));
    public override Size GetPreferredSize(Size proposedSize)
        => new(TextRenderer.MeasureText(Text, Font).Width + GlyphSize + 8,
            Math.Max(Font.Height, GlyphSize) + 4);
    protected override void OnPaint(PaintEventArgs e)
    {
        if (SystemInformation.HighContrast) { base.OnPaint(e); return; }
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var size = GlyphSize;
        using var shape = SettingsShape.Rounded(new RectangleF(1, (Height - size) / 2F, size - 1, size - 1), 3);
        using var fill = new SolidBrush(Checked && Enabled ? UiDesignTokens.Accent :
            _hovered && Enabled ? UiDesignTokens.AccentSoft : UiDesignTokens.SurfaceSubtle);
        using var border = new Pen(Checked && Enabled ? UiDesignTokens.Accent : UiDesignTokens.BorderStrong);
        e.Graphics.FillPath(fill, shape);
        e.Graphics.DrawPath(border, shape);
        if (CheckState != CheckState.Unchecked)
        {
            using var tick = new Pen(Enabled && Checked ? Color.White : UiDesignTokens.TextMuted, 1.8F * DeviceDpi / 96F)
            { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            var y = (Height - size) / 2F;
            if (CheckState == CheckState.Indeterminate)
                e.Graphics.DrawLine(tick, size * .27F, y + size * .5F, size * .73F, y + size * .5F);
            else e.Graphics.DrawLines(tick, new[] { new PointF(size * .25F, y + size * .5F),
                new PointF(size * .44F, y + size * .7F), new PointF(size * .78F, y + size * .3F) });
        }
        var textBounds = new Rectangle(size + 7, 0, Math.Max(1, Width - size - 7), Height);
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds,
            Enabled ? ForeColor : UiDesignTokens.TextMuted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, textBounds);
    }
    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
    protected override void OnCheckStateChanged(EventArgs e) { base.OnCheckStateChanged(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { _hovered = true; base.OnMouseEnter(e); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; base.OnMouseLeave(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
}
