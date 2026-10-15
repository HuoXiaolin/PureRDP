using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PureRDP.UI;

// Keep the native combo/edit/list windows and their keyboard/accessibility
// behavior. Only the colors, list rows and closed-field chrome are painted.
internal sealed class ModernComboBox : ComboBox
{
    private bool _hovered;

    public ModernComboBox()
    {
        FlatStyle = FlatStyle.Flat;
        DrawMode = DrawMode.OwnerDrawFixed;
        BackColor = UiDesignTokens.SurfaceSubtle;
        ForeColor = UiDesignTokens.TextPrimary;
        ItemHeight = Math.Max(20, Font.Height + 4);
        IntegralHeight = true;
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        ItemHeight = Math.Max(20, Font.Height + 4);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Bounds.Width <= 0 || e.Bounds.Height <= 0) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        var edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
        var background = selected && !edit ? UiDesignTokens.AccentSoft : BackColor;
        var ink = !Enabled ? UiDesignTokens.TextMuted : selected && !edit
            ? UiDesignTokens.Accent : ForeColor;
        using var brush = new SolidBrush(background);
        e.Graphics.FillRectangle(brush, e.Bounds);
        var text = e.Index >= 0 && e.Index < Items.Count ? GetItemText(Items[e.Index]) : Text;
        var bounds = Rectangle.Inflate(e.Bounds, -5, 0);
        TextRenderer.DrawText(e.Graphics, text, Font, bounds, ink,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (!edit && (e.State & DrawItemState.Focus) != 0)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(e.Bounds, -2, -2), ink, background);
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; base.OnMouseEnter(e); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; base.OnMouseLeave(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnDropDown(EventArgs e) { base.OnDropDown(e); Invalidate(); }
    protected override void OnDropDownClosed(EventArgs e) { base.OnDropDownClosed(e); Invalidate(); }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (DropDownStyle == ComboBoxStyle.Simple || !IsHandleCreated) return;
        if (m.Msg == 0x000F) // WM_PAINT
        {
            using var graphics = Graphics.FromHwnd(Handle);
            PaintChrome(graphics);
        }
        else if (m.Msg is 0x0317 or 0x0318 && m.WParam != IntPtr.Zero) // WM_PRINT / WM_PRINTCLIENT
        {
            using var graphics = Graphics.FromHdc(m.WParam);
            PaintChrome(graphics);
        }
    }

    private void PaintChrome(Graphics graphics)
    {
        if (Width < 4 || Height < 4 || SystemInformation.HighContrast) return;
        // Erase the remaining native inner bevel before drawing a single
        // flat outline; do not cover the edit window or selected text.
        using (var surface = new SolidBrush(BackColor))
        {
            graphics.FillRectangle(surface, 0, 0, Width, 2);
            graphics.FillRectangle(surface, 0, Height - 2, Width, 2);
            graphics.FillRectangle(surface, 0, 0, 2, Height);
            graphics.FillRectangle(surface, Width - 2, 0, 2, Height);
        }
        var scale = DeviceDpi / 96F;
        var arrowWidth = Math.Min(Width - 2, (int)Math.Round(24 * scale));
        var area = new Rectangle(Width - arrowWidth - 1, 1, arrowWidth, Height - 2);
        using var fill = new SolidBrush(Enabled && (_hovered || DroppedDown)
            ? UiDesignTokens.AccentSoft : UiDesignTokens.SurfaceSoft);
        graphics.FillRectangle(fill, area);
        using var border = new Pen(Enabled && (Focused || DroppedDown)
            ? UiDesignTokens.Accent : UiDesignTokens.BorderStrong);
        graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var arrow = new Pen(Enabled ? UiDesignTokens.TextSecondary : UiDesignTokens.TextMuted, 1.5F * scale)
        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        var x = area.Left + area.Width / 2F;
        var y = area.Top + area.Height / 2F;
        graphics.DrawLines(arrow, new[] { new PointF(x - 3 * scale, y - scale),
            new PointF(x, y + 2 * scale), new PointF(x + 3 * scale, y - scale) });
    }
}
