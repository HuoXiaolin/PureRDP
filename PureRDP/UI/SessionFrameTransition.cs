using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PureRDP.UI;

// A sibling of the ActiveX and loading panels. Only this child changes opacity.
internal sealed class SessionFrameTransition : Control
{
    private const int FadeMilliseconds = 150;
    private const int MaxWaitMilliseconds = 160;
    private readonly Timer _timer = new() { Interval = 15 };
    private Bitmap? _frame;
    private long _fadeAt;
    private bool _layerUnavailable;

    internal SessionFrameTransition()
    {
        SetStyle(ControlStyles.Selectable, false);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Fill;
        TabStop = false;
        Visible = false;
        _timer.Tick += Advance;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x00080000 | 0x00000020; // WS_EX_LAYERED | WS_EX_TRANSPARENT
            return parameters;
        }
    }

    internal static bool AnimationsEnabled =>
        SystemParametersInfoW(0x1042, 0, out var animate, 0) && animate; // SPI_GETCLIENTAREAANIMATION

    internal void Begin(Control source, bool waitForDisplay)
    {
        if (IsDisposed || _layerUnavailable || _frame != null || !source.IsHandleCreated || !source.Visible)
            return;

        try
        {
            if (!SetLayeredWindowAttributes(Handle, 0, byte.MaxValue, 2)) return; // LWA_ALPHA
        }
        catch (Win32Exception)
        {
            // Hosts without layered-child support still show the live canvas.
            _layerUnavailable = true;
            return;
        }
        _frame = CaptureClient(source);
        // A hardware-rendered native child can be printable only as part
        // of its owning window's composed client area.
        if (_frame == null && Parent is Form host && source.Parent == host &&
            source.Bounds == host.ClientRectangle)
            _frame = CaptureClient(host);
        if (_frame == null) return;
        BackColor = source.BackColor;
        BringToFront();
        Show();
        Update();
        _fadeAt = Environment.TickCount64 + (waitForDisplay ? MaxWaitMilliseconds : 16);
        _timer.Start();
    }

    internal void Reveal()
        => _fadeAt = Math.Min(_fadeAt, Environment.TickCount64);

    private void Advance(object? sender, EventArgs e)
    {
        var elapsed = Environment.TickCount64 - _fadeAt;
        if (elapsed < 0) return;
        if (elapsed >= FadeMilliseconds || !Visible || !IsHandleCreated)
        {
            Cancel();
            return;
        }

        var t = (double)elapsed / FadeMilliseconds;
        var alpha = (byte)Math.Round(255 * (1 - t * t * (3 - 2 * t)));
        if (!SetLayeredWindowAttributes(Handle, 0, alpha, 2))
            Cancel();
    }

    private static Bitmap? CaptureClient(Control source)
    {
        var size = source.ClientSize;
        if (size.Width <= 0 || size.Height <= 0 || (long)size.Width * size.Height > 16_777_216)
            return null;

        Bitmap? bitmap = null;
        try
        {
            bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                var dc = graphics.GetHdc();
                try
                {
                    // Print this session's own child HWND, even if another app covers it.
                    if (!PrintWindow(source.Handle, dc, 3)) return null; // PW_CLIENTONLY | PW_RENDERFULLCONTENT
                }
                finally { graphics.ReleaseHdc(dc); }
            }
            if (!ContainsPicture(bitmap)) return null;
            var result = bitmap;
            bitmap = null;
            return result;
        }
        catch (Exception ex) when (ex is ExternalException or OutOfMemoryException or ArgumentException)
        {
            return null;
        }
        finally { bitmap?.Dispose(); }
    }

    private static bool ContainsPicture(Bitmap image)
    {
        // An otherwise black desktop may only have icons or a taskbar at an edge.
        var data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, image.PixelFormat);
        try
        {
            var row = new int[image.Width];
            var step = Math.Max(1, image.Height / 96);
            for (var y = 0; y < image.Height; y += step)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                foreach (var pixel in row)
                    if ((pixel & 0xffffff) != 0) return true;
            }
            return false;
        }
        finally { image.UnlockBits(data); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_frame == null || ClientSize.IsEmpty) return;
        if (_frame.Size == ClientSize)
        {
            e.Graphics.DrawImageUnscaled(_frame, Point.Empty);
            return;
        }

        var ratio = Math.Min((double)Width / _frame.Width, (double)Height / _frame.Height);
        var width = Math.Max(1, (int)Math.Round(_frame.Width * ratio));
        var height = Math.Max(1, (int)Math.Round(_frame.Height * ratio));
        e.Graphics.Clear(BackColor);
        e.Graphics.DrawImage(_frame, new Rectangle(0, 0, width, height), 0, 0,
            _frame.Width, _frame.Height, GraphicsUnit.Pixel);
    }

    internal void Cancel()
    {
        _timer.Stop();
        Hide();
        _frame?.Dispose();
        _frame = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            _frame?.Dispose();
            _frame = null;
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr window, uint color, byte alpha, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SystemParametersInfoW(uint action, uint parameter,
        [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);
}
