using System;
using System.Collections.Generic;
using System.Globalization;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PureRDP.Configuration.Enums;

namespace PureRDP.UI;

// Windows composites each complete alpha frame separately from the native RDP surface.
internal sealed class GlassWindowControlStrip : Form
{
    internal sealed record DeviceButton(string Name, bool Selected, Action Activate)
    {
        internal Color AccentColor { get; init; } = UiDesignTokens.Accent;
    }
    private IReadOnlyList<DeviceButton> _devices = Array.Empty<DeviceButton>();
    private int ButtonCount => 4 + _devices.Count;
    private readonly Timer _collapseTimer;
    private readonly Timer _maximizeClickTimer;
    private bool _isExpanded, _maximized;
    private bool _autoHide = true;
    private bool _maximizeClickPending;
    private bool _doubleMaximizePress;
    private int _hovered = -1, _pressed = -1;
    private readonly int? _resizeCorner;
    private ControlBarPosition _position = ControlBarPosition.Right;
    public GlassWindowControlStrip() : this(null) { }
    public GlassWindowControlStrip(int? resizeCorner)
    {
        _resizeCorner = resizeCorner;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Size = resizeCorner.HasValue ? new Size(16, 16) : new Size(12, 58);
        SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
        _collapseTimer = new Timer { Interval = 620 };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!_autoHide) return;
            if (!IsInteractivePoint(Cursor.Position)) SetExpanded(false);
        };
        _maximizeClickTimer = new Timer { Interval = SystemInformation.DoubleClickTime };
        _maximizeClickTimer.Tick += (_, _) =>
        {
            _maximizeClickTimer.Stop();
            if (!_maximizeClickPending) return;
            _maximizeClickPending = false;
            MaximizeRequested?.Invoke(this, EventArgs.Empty);
        };
    }
    public event EventHandler? ManagementRequested, MinimizeRequested, MaximizeRequested, CloseRequested;
    public event EventHandler? ToggleFullScreenRequested, ExpandedChanged;
    public bool IsExpanded => _isExpanded;
    internal int LastCompositionError { get; private set; }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var value = base.CreateParams;
            value.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000;
            return value;
        }
    }
    public void SetGlassMode(bool enabled) => RenderSurface();
    public void UpdateMaximizeGlyph(bool value) { _maximized = value; RenderSurface(); }
    internal void SetAutoHide(bool value)
    {
        if (_resizeCorner.HasValue) return;
        _autoHide = value;
        _collapseTimer.Stop();
        if (!value)
            SetExpanded(true);
        else if (!Bounds.Contains(Cursor.Position))
            SetExpanded(false);
    }
    internal void SetPosition(ControlBarPosition position)
    {
        if (_position == position || _resizeCorner.HasValue) return;
        _position = position;
        _hovered = _pressed = -1;
        Size = _isExpanded ? ExpandedSize : CollapsedSize;
        UpdateHitRegion();
        RenderSurface();
    }
    internal void SetDevices(IReadOnlyList<DeviceButton> devices)
    {
        if (IsDisposed || _resizeCorner.HasValue) return;
        _devices = devices;
        _hovered = _pressed = -1;
        if (_isExpanded) Size = ExpandedSize;
        UpdateHitRegion();
        ExpandedChanged?.Invoke(this, EventArgs.Empty);
        RenderSurface();
    }
    public void Expand() { if (_resizeCorner.HasValue) return; _collapseTimer.Stop(); SetExpanded(true); }
    public void Collapse() { if (!Bounds.Contains(Cursor.Position)) SetExpanded(false); }
    private void SetExpanded(bool value)
    {
        if (_resizeCorner.HasValue || _isExpanded == value || (!value && !_autoHide)) return;
        _isExpanded = value;
        _hovered = _pressed = -1;
        Size = value ? ExpandedSize : CollapsedSize;
        UpdateHitRegion();
        ExpandedChanged?.Invoke(this, EventArgs.Empty);
        RenderSurface();
    }
    private void CancelPendingMaximizeClick()
    {
        _maximizeClickTimer.Stop();
        _maximizeClickPending = false;
        _doubleMaximizePress = false;
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdateHitRegion(); RenderSurface(); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); UpdateHitRegion(); RenderSurface(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Expand(); }
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = -1;
        RenderSurface();
        if (_autoHide) _collapseTimer.Start();
    }
    private bool IsHorizontal => _position is ControlBarPosition.Top or ControlBarPosition.Bottom;
    private Size CollapsedSize => IsHorizontal ? new Size(58, 12) : new Size(12, 58);
    private Size ExpandedSize => IsHorizontal
        ? new Size(14 + ButtonCount * 30, 44)
        : new Size(44, 14 + ButtonCount * 30);
    private Rectangle ButtonBounds(int i) => IsHorizontal
        ? new Rectangle(8 + i * 30, 8, 28, 28)
        : new Rectangle(8, 8 + i * 30, 28, 28);
    private static Color ControlAccent(int index, bool isClose) => isClose
        ? Color.FromArgb(198, 70, 76)
        : index switch
        {
            0 => Color.FromArgb(45, 126, 175),  // console
            1 => Color.FromArgb(194, 132, 42),  // minimize
            2 => Color.FromArgb(52, 145, 96),   // maximize
            _ => Color.FromArgb(38, 58, 74),
        };
    private int ButtonAt(Point p)
    {
        if (_isExpanded) for (var i = 0; i < ButtonCount; i++) if (ButtonBounds(i).Contains(p)) return i;
        return -1;
    }
    private Point ScreenPointFromMessage(Message message)
    {
        var packed = message.LParam.ToInt64();
        return PointToClient(new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff)));
    }

    internal bool IsInteractivePoint(Point screenPoint)
    {
        var point = PointToClient(screenPoint);
        if (!Visible || IsDisposed || !ClientRectangle.Contains(point))
            return false;

        // The collapsed rail is the hover/click target. Once expanded, only
        // actual buttons should sit above the remote desktop; the remaining
        // transparent padding must let remote mouse input pass through.
        return !_isExpanded || ButtonAt(point) >= 0;
    }

    private void UpdateHitRegion()
    {
        if (IsDisposed || Width <= 0 || Height <= 0)
            return;

        var region = new Region(ClientRectangle);
        if (_isExpanded && !_resizeCorner.HasValue)
        {
            region.MakeEmpty();
            for (var i = 0; i < ButtonCount; i++)
                region.Union(ButtonBounds(i));
        }

        var previous = Region;
        Region = region;
        previous?.Dispose();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0021) // WM_MOUSEACTIVATE
        {
            m.Result = (IntPtr)3; // MA_NOACTIVATE
            return;
        }

        if (m.Msg == 0x0084 && _isExpanded && !_resizeCorner.HasValue &&
            ButtonAt(ScreenPointFromMessage(m)) < 0)
        {
            // Let the underlying RDP child receive clicks in the expanded
            // strip's padding instead of blocking the whole narrow overlay.
            m.Result = (IntPtr)(-1); // HTTRANSPARENT
            return;
        }

        try
        {
            base.WndProc(ref m);
        }
        finally
        {
            // WinForms implicitly captures on mouse-down, including clicks
            // which only expand this non-activating overlay. Never retain
            // capture: a release outside the rail belongs to the RDP window.
            if (m.Msg is 0x0201 or 0x0203 or 0x0204 or 0x0207 && Capture)
                Capture = false;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var button = ButtonAt(e.Location);
        // e.Clicks is Windows' authoritative double-click classification. Do
        // not require our delayed single-click timer to still be pending: its
        // WM_TIMER can be processed just before the second click arrives.
        _doubleMaximizePress = e.Clicks >= 2 && button == 2;
        if (_doubleMaximizePress)
        {
            _maximizeClickTimer.Stop();
            _maximizeClickPending = false;
        }
        else if (button != 2)
            CancelPendingMaximizeClick();
        if (_resizeCorner is int corner && Owner is FormRdpClient owner)
        {
            CancelPendingMaximizeClick();
            owner.BeginRemoteWindowResize(corner);
            return;
        }
        _pressed = ButtonAt(e.Location);
        // The matching release must land on the same button; no mouse capture.
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_resizeCorner is int corner)
        {
            Cursor = corner is Platform.Interop.HTTOPLEFT or Platform.Interop.HTBOTTOMRIGHT ? Cursors.SizeNWSE : Cursors.SizeNESW;
            return;
        }
        var hovered = ButtonAt(e.Location);
        if (_hovered != hovered) { _hovered = hovered; RenderSurface(); }
        Cursor = hovered >= 0 ? Cursors.Hand : Cursors.Default;
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var pressed = _pressed;
        _pressed = -1;
        if (Capture)
            Capture = false;
        if (e.Button != MouseButtons.Left || pressed < 0 || pressed != ButtonAt(e.Location)) return;
        if (pressed == 0)
        {
            CancelPendingMaximizeClick();
            ManagementRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (pressed == 1)
        {
            CancelPendingMaximizeClick();
            MinimizeRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (pressed == 2)
        {
            // MouseUp reports one click even after WM_LBUTTONDBLCLK.
            // Preserve the double-click classification from MouseDown.
            if (_doubleMaximizePress)
            {
                _doubleMaximizePress = false;
                ToggleFullScreenRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                _maximizeClickPending = true;
                _maximizeClickTimer.Start();
            }
        }
        else if (pressed >= 3 && pressed < 3 + _devices.Count)
        {
            CancelPendingMaximizeClick();
            _devices[pressed - 3].Activate();
        }
        else
        {
            CancelPendingMaximizeClick();
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }
    private void RenderSurface()
    {
        if (!IsHandleCreated || IsDisposed || Width <= 0 || Height <= 0) return;
        using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            // Clear every frame; never paint translucent strokes over a previous frame.
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var background = new SolidBrush(Color.FromArgb(_resizeCorner.HasValue ? 1 : 24, UiDesignTokens.PageBackground));
            g.FillRectangle(background, 0, 0, Width, Height);
            using var grip = new Pen(Color.FromArgb(95, 100, 125, 140), 1F)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            if (!_resizeCorner.HasValue)
            {
                if (IsHorizontal) g.DrawLine(grip, 15, 5, Width - 15, 5);
                else g.DrawLine(grip, 5, 15, 5, Height - 15);
            }
            if (_isExpanded) for (var i = 0; i < ButtonCount; i++)
            {
                var r = ButtonBounds(i);
                var iconBounds = new RectangleF(r.X + 2F, r.Y + 2F, r.Width - 4F, r.Height - 4F);
                var isClose = i == ButtonCount - 1;
                var accent = ControlAccent(i, isClose);

                // Every control icon has the same soft circular canvas. It
                // makes the narrow toolbar read as one coherent modern
                // control while leaving the original button hit rectangles
                // untouched.
                var iconSurface = _hovered == i
                    ? Color.FromArgb(164, accent)
                    : Color.FromArgb(50, accent);
                using (var surface = new SolidBrush(iconSurface))
                    g.FillEllipse(surface, iconBounds);
                if (_hovered == i)
                {
                    using var ring = new Pen(Color.FromArgb(205, accent), 1F);
                    g.DrawEllipse(ring, iconBounds);
                }
                // Vector strokes with rounded caps/joins stay smooth at DPI
                // scales and avoid the old one-pixel staircase on diagonals.
                using var pen = new Pen(Color.FromArgb(245, accent), 1.65F)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round,
                };
                var x = r.X + 8; var y = r.Y + 8;
                if (i == 0)
                {
                    using var home = new GraphicsPath();
                    home.AddLines(new[]
                    {
                        new PointF(x + 1F, y + 7F),
                        new PointF(x + 6F, y + 2F),
                        new PointF(x + 11F, y + 7F),
                    });
                    g.DrawPath(pen, home);
                    DrawRoundedGlyph(g, pen, new RectangleF(x + 3F, y + 7F, 6F, 6F), 1.2F);
                    g.DrawLine(pen, x + 6, y + 10, x + 6, y + 13);
                }
                else if (i == 1) g.DrawLine(pen, x, y + 8, x + 12, y + 8);
                else if (i == 2 && !_maximized)
                    DrawRoundedGlyph(g, pen, new RectangleF(x + 0.5F, y + 0.5F, 11F, 11F), 1.5F);
                else if (i == 2)
                {
                    g.DrawLines(pen, new[]
                    {
                        new PointF(x + 3F, y + 2F), new PointF(x + 3F, y),
                        new PointF(x + 12F, y), new PointF(x + 12F, y + 9F),
                        new PointF(x + 10F, y + 9F),
                    });
                    DrawRoundedGlyph(g, pen, new RectangleF(x + 0.5F, y + 3.5F, 8.5F, 8.5F), 1.2F);
                }
                else if (i >= 3 && i < 3 + _devices.Count)
                {
                    var device = _devices[i - 3];
                    using var fill = new SolidBrush(device.AccentColor);
                    g.FillEllipse(fill, iconBounds);
                    using var deviceBorder = new Pen(device.Selected
                        ? Color.White
                        : Color.FromArgb(170, DeviceAccentColors.GetContrastingText(device.AccentColor)), device.Selected ? 1.5F : 1F);
                    g.DrawEllipse(deviceBorder, iconBounds);
                    using var font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold, GraphicsUnit.Pixel);
                    using var ink = new SolidBrush(DeviceAccentColors.GetContrastingText(device.AccentColor));
                    using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(StringInfo.GetNextTextElement(string.IsNullOrWhiteSpace(device.Name) ? "?" : device.Name.Trim()), font, ink, iconBounds, format);
                }
                else { g.DrawLine(pen, x, y, x + 12, y + 12); g.DrawLine(pen, x + 12, y, x, y + 12); }
            }
        }
        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        var previous = SelectObject(memoryDc, hBitmap);
        try
        {
            var destination = Location; var source = Point.Empty; var size = Size;
            var blend = new BlendFunction { SourceConstantAlpha = 255, AlphaFormat = 1 };
            LastCompositionError = UpdateLayeredWindow(Handle, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, 2)
                ? 0 : Marshal.GetLastWin32Error();
            if (LastCompositionError != 0)
                System.Diagnostics.Trace.WriteLine($"Toolbar composition failed: {LastCompositionError}");
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(hBitmap); DeleteDC(memoryDc); ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
    private static GraphicsPath RoundedGlyphPath(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return path;

        var diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), Math.Max(2F, radius * 2F));
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
    private static void DrawRoundedGlyph(Graphics graphics, Pen pen, RectangleF bounds, float radius)
    {
        using var path = RoundedGlyphPath(bounds, radius);
        graphics.DrawPath(pen, path);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _collapseTimer?.Dispose();
            _maximizeClickTimer?.Dispose();
            _devices = Array.Empty<DeviceButton>();
        }
        base.Dispose(disposing);
    }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr dc, ref Point destination, ref Size size, IntPtr sourceDc, ref Point source, uint key, ref BlendFunction blend, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
