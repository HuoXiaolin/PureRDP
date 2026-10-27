using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PureRDP.UI;

// Native WinForms adaptation of the Uiverse.io crystal loader by Z4drus,
// supplied by the user. Combine rotation and emergence instead of competing
// CSS transform animations. No browser runtime or image-frame allocations.
internal sealed class CrystalLoaderControl : Control
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private readonly Stopwatch _clock = new();
    private Form? _owner;
    private bool _loading;
    private static readonly Color[] Dark = {
        Color.FromArgb(0, 51, 102), Color.FromArgb(0, 51, 153),
        Color.FromArgb(0, 102, 204), Color.FromArgb(0, 153, 255),
        Color.FromArgb(51, 204, 255), Color.FromArgb(102, 255, 255) };
    private static readonly Color[] Light = {
        Color.FromArgb(51, 102, 153), Color.FromArgb(51, 102, 204),
        Color.FromArgb(51, 153, 255), Color.FromArgb(102, 204, 255),
        Color.FromArgb(153, 204, 255), Color.FromArgb(204, 255, 255) };

    internal CrystalLoaderControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(200, 200);
        TabStop = false;
        AccessibleName = "正在连接，蓝色水晶加载动画";
        _timer.Tick += Animate;
    }

    private void Animate(object? sender, EventArgs e)
    {
        UpdateAnimation();
        if (_timer.Enabled) Invalidate();
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        ObserveOwner();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ObserveOwner();
    }

    private void ObserveOwner()
    {
        var owner = FindForm();
        if (_owner != owner)
        {
            if (_owner != null) _owner.SizeChanged -= OwnerSizeChanged;
            _owner = owner;
            if (_owner != null) _owner.SizeChanged += OwnerSizeChanged;
        }
        UpdateAnimation();
    }

    private void OwnerSizeChanged(object? sender, EventArgs e) => UpdateAnimation();

    internal void SetLoading(bool loading)
    {
        _loading = loading;
        UpdateAnimation();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateAnimation();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _timer.Stop();
        _clock.Stop();
        base.OnHandleDestroyed(e);
    }

    private void UpdateAnimation()
    {
        if (_loading && !IsDisposed && IsHandleCreated && Visible &&
            _owner?.WindowState != FormWindowState.Minimized)
        {
            _clock.Start();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            _clock.Stop();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        PaintFrame(e.Graphics, _clock.Elapsed.TotalSeconds);
    }

    internal void PaintFrame(Graphics graphics, double seconds)
    {
        var original = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TranslateTransform(Width / 2f, Height / 2f);
            var size = Math.Min(Width, Height) / 200f;
            graphics.ScaleTransform(size, size);
            for (var i = 0; i < 6; i++)
            {
                var elapsed = seconds - i * .3;
                if (elapsed < 0) continue;
                var pulse = (1 - Math.Cos(elapsed * Math.PI)) / 2;
                var scale = (float)(.5 + .5 * pulse);
                var opacity = (int)(204 * Math.Min(1, elapsed / .3) * (.25 + .75 * pulse));
                var layer = graphics.Save();
                graphics.ScaleTransform(scale, scale * .7071f);
                graphics.RotateTransform((float)(elapsed * 90));
                using var path = new GraphicsPath();
                path.AddArc(-30, -30, 20, 20, 180, 90);
                path.AddArc(10, -30, 20, 20, 270, 90);
                path.AddArc(10, 10, 20, 20, 0, 90);
                path.AddArc(-30, 10, 20, 20, 90, 90);
                path.CloseFigure();
                using var glow = new Pen(Color.FromArgb(opacity / 8, Light[i]), 12);
                graphics.DrawPath(glow, path);
                using var fill = new LinearGradientBrush(new Rectangle(-30, -30, 60, 60),
                    Color.FromArgb(opacity, Dark[i]), Color.FromArgb(opacity, Light[i]), 45f);
                graphics.FillPath(fill, path);
                using var edge = new Pen(Color.FromArgb(opacity / 2, Light[i]), 1);
                graphics.DrawPath(edge, path);
                graphics.Restore(layer);
            }
        }
        finally { graphics.Restore(original); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_owner != null) _owner.SizeChanged -= OwnerSizeChanged;
            _owner = null;
            _timer.Stop();
            _timer.Tick -= Animate;
            _timer.Dispose();
            _clock.Stop();
        }
        base.Dispose(disposing);
    }
}
