using PureRDP.Configuration;
using PureRDP.Platform;
using PureRDP.Services;
using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PureRDP.UI;

// An existing interactive RDP window is mirrored with a DWM thumbnail. If no
// interactive window exists, PreviewSessionHost renders one read-only session
// directly inside this card. Neither path creates a second top-level window.
internal sealed class RemotePreviewControl : Control
{
    private readonly Timer _timer = new() { Interval = 250 };
    private IntPtr _thumbnail;
    private Form? _sourceForm;
    private PreviewSessionHost? _sessionHost;
    private NativeRect _lastThumbnailDestination;
    private Size _lastThumbnailSourceSize;
    private bool _thumbnailPropertiesApplied;
    private string _message = "点击“预览”查看";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<Form?>? SourceProvider { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<PreviewSessionHost?>? PreviewHostProvider { get; set; }
    internal bool HasThumbnail => _thumbnail != IntPtr.Zero;

    public RemotePreviewControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = UiDesignTokens.SurfaceSoft;
        ForeColor = UiDesignTokens.TextMuted;
        Cursor = Cursors.Default;
        TabStop = false;
        AccessibleName = "预览窗";
        AccessibleRole = AccessibleRole.Graphic;
        _timer.Tick += (_, _) => RefreshPreview();
    }

    internal void RefreshPreview()
    {
        if (IsDisposed || !Visible || !IsHandleCreated) return;
        var destination = FindForm();
        if (destination == null || !IsPreviewRectangleVisible(destination))
        {
            ReleaseThumbnail();
            _timer.Interval = 1000;
            return;
        }
        _timer.Interval = _sessionHost != null ? 1000 : 250;

        var source = SourceProvider?.Invoke();
        if (source != null && !source.IsDisposed && source.IsHandleCreated)
        {
            DetachSessionHost();
            RefreshThumbnail(source);
            return;
        }

        ReleaseThumbnail();
        var host = PreviewHostProvider?.Invoke();
        if (host is { IsDisposed: false })
        {
            if (!ReferenceEquals(_sessionHost, host))
            {
                DetachSessionHost();
                _sessionHost = host;
            }

            host.AttachTo(this);
            if (_message.Length != 0)
            {
                _message = string.Empty;
                Invalidate();
            }
            return;
        }

        DetachSessionHost();
        ShowStatus("点击“预览”查看");
    }

    private void RefreshThumbnail(Form source)
    {
        var destination = FindForm();
        if (destination == null || destination.IsDisposed || !destination.IsHandleCreated ||
            destination.WindowState == FormWindowState.Minimized)
        {
            ReleaseThumbnail();
            return;
        }

        if (_thumbnail == IntPtr.Zero || _sourceForm != source ||
            !IsPreviewRectangleVisible(destination))
        {
            if (!IsPreviewRectangleVisible(destination))
            {
                ReleaseThumbnail();
                return;
            }

            ReleaseThumbnail();
            if (DwmRegisterThumbnail(destination.Handle, source.Handle, out _thumbnail) != 0)
            {
                ShowStatus("预览暂不可用");
                return;
            }
            _sourceForm = source;
        }

        var sourceSize = source.ClientSize;
        if (sourceSize.Width <= 0 || sourceSize.Height <= 0)
            sourceSize = source.RestoreBounds.Size;
        if (sourceSize.Width <= 0 || sourceSize.Height <= 0)
            return;

        var scale = Math.Min((double)Width / sourceSize.Width, (double)Height / sourceSize.Height);
        var size = new Size(Math.Max(1, (int)(sourceSize.Width * scale)),
            Math.Max(1, (int)(sourceSize.Height * scale)));
        var point = destination.PointToClient(PointToScreen(new Point(
            (Width - size.Width) / 2, (Height - size.Height) / 2)));
        var destinationRect = new NativeRect
        {
            Left = point.X, Top = point.Y,
            Right = point.X + size.Width, Bottom = point.Y + size.Height,
        };
        if (_thumbnailPropertiesApplied &&
            _lastThumbnailDestination.Equals(destinationRect) &&
            _lastThumbnailSourceSize == sourceSize)
            return;

        var properties = new ThumbnailProperties
        {
            Flags = 1 | 4 | 8 | 16,
            Destination = destinationRect,
            Opacity = 255,
            Visible = true,
            ClientOnly = true,
        };
        if (DwmUpdateThumbnailProperties(_thumbnail, ref properties) != 0)
            ShowStatus("预览暂不可用");
        else
        {
            _lastThumbnailDestination = destinationRect;
            _lastThumbnailSourceSize = sourceSize;
            _thumbnailPropertiesApplied = true;
        }
        if (_message.Length != 0)
        {
            _message = string.Empty;
            Invalidate();
        }
    }

    private bool IsPreviewRectangleVisible(Form destination)
    {
        var screenBounds = RectangleToScreen(ClientRectangle);
        for (Control? parent = Parent; parent != null; parent = parent.Parent)
        {
            if (!parent.Visible || !parent.RectangleToScreen(parent.ClientRectangle).Contains(screenBounds))
                return false;
        }
        return destination.WindowState != FormWindowState.Minimized;
    }

    private void ShowStatus(string message)
    {
        ReleaseThumbnail();
        if (_message == message) return;
        _message = message;
        Invalidate();
    }

    private void ReleaseThumbnail()
    {
        if (_thumbnail != IntPtr.Zero)
            DwmUnregisterThumbnail(_thumbnail);
        _thumbnail = IntPtr.Zero;
        _sourceForm = null;
        _lastThumbnailDestination = default;
        _lastThumbnailSourceSize = default;
        _thumbnailPropertiesApplied = false;
    }

    private void DetachSessionHost()
    {
        if (_sessionHost == null) return;
        _sessionHost.DetachFrom(this);
        _sessionHost = null;
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) { _timer.Start(); RefreshPreview(); }
        // A view switch hides only the viewport. Keep the ActiveX surface in
        // its existing parent so its COM wrapper and live session survive.
        else { _timer.Stop(); ReleaseThumbnail(); }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (Visible) { _timer.Start(); RefreshPreview(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_thumbnail != IntPtr.Zero || _sessionHost != null) return;
        TextRenderer.DrawText(e.Graphics, _message, Font, ClientRectangle, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        ReleaseThumbnail();
        DetachSessionHost();
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            ReleaseThumbnail();
            DetachSessionHost();
        }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)] private struct ThumbnailProperties
    {
        public uint Flags;
        public NativeRect Destination, Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool ClientOnly;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(
        IntPtr destination, IntPtr source, out IntPtr thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(
        IntPtr thumbnail, ref ThumbnailProperties properties);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
}

/// <summary>
/// A single, read-only RDP session hosted inside a card preview. It remains
/// alive when the card is temporarily hidden and can be transferred to the
/// real connection window without reconnecting.
/// </summary>
internal sealed class PreviewSessionHost : IMessageFilter, IDisposable
{
    private readonly ConnectionSettings _settings;
    private RdpClientService? _service;
    private Panel? _surface;
    private Label? _status;
    private RemotePreviewControl? _viewport;
    private bool _filterInstalled;
    private bool _disposed;

    internal PreviewSessionHost(ConnectionSettings settings)
    {
        _settings = settings.Clone();
    }

    internal bool IsDisposed => _disposed;
    internal ConnectionQualitySnapshot? ConnectionQuality => _service?.ConnectionQuality;
    internal Task RefreshConnectionQualityAsync()
        => !_disposed && _service != null ? _service.RefreshConnectionQualityAsync() : Task.CompletedTask;

    internal bool PrepareForInteractive(ConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_disposed || _service == null)
            return false;

        // Preview sessions deliberately disable redirection and multimonitor
        // support. Those values are negotiated when RDP connects and cannot
        // be reliably changed after login. Reconnect this same hosted session
        // only when the real connection needs one of those settings.
        try
        {
            if (settings.UseAllMonitors == true ||
                settings.RedirectAudioCapture == true ||
                settings.AudioPlaybackMode != Configuration.Enums.AudioPlaybackMode.DoNotPlay ||
                settings.RedirectClipboard == true ||
                settings.RedirectComPorts == true ||
                settings.RedirectPrinters == true ||
                settings.RedirectLocation == true ||
                settings.RedirectWebAuthn == true ||
                !string.IsNullOrWhiteSpace(settings.RedirectDrives) ||
                !string.IsNullOrWhiteSpace(settings.RedirectCameras) ||
                !string.IsNullOrWhiteSpace(settings.RedirectPnpDevices))
            {
                _service.Reconnect(settings);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The caller can fall back to a fresh interactive connection if
            // the preview ActiveX instance cannot be reconfigured safely.
            return false;
        }

        return true;
    }

    internal void AttachTo(RemotePreviewControl viewport)
    {
        if (_disposed) return;
        if (ReferenceEquals(_viewport, viewport))
        {
            if (_surface is { Visible: false }) EnsureSurfaceVisible();
            return;
        }

        DetachFrom(_viewport);
        _viewport = viewport;
        EnsureSurface();

        if (_surface!.Parent != viewport)
            viewport.Controls.Add(_surface);
        _surface.Dock = DockStyle.Fill;
        _surface.BringToFront();
        _status?.BringToFront();

        if (_service == null)
            StartSession();
        else
            EnsureInputFilter();
    }

    internal void DetachFrom(RemotePreviewControl? viewport)
    {
        if (viewport != null && !ReferenceEquals(_viewport, viewport)) return;
        _viewport = null;
        if (_surface?.Parent != null)
            _surface.Parent.Controls.Remove(_surface);
        _surface?.Hide();
    }

    internal bool TryTakeService(out RdpClientService service, out Panel? oldSurface)
    {
        service = null!;
        oldSurface = null;
        if (_disposed || _service == null)
            return false;

        RemoveInputFilter();
        if (_surface?.Parent != null)
            _surface.Parent.Controls.Remove(_surface);

        service = _service;
        oldSurface = _surface;
        service.Connected -= Service_Connected;
        service.LoginCompleted -= Service_LoginCompleted;
        service.Disconnected -= Service_Disconnected;
        _service = null;
        _surface = null;
        _status = null;
        _viewport = null;
        _disposed = true;
        return true;
    }

    private void EnsureSurface()
    {
        if (_surface == null || _surface.IsDisposed)
        {
            _surface = new Panel
            {
                BackColor = Color.Black,
                Dock = DockStyle.Fill,
                TabStop = false,
            };
            _status = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(235, 242, 249, 245),
                ForeColor = UiDesignTokens.TextMuted,
                Font = new Font("Microsoft YaHei UI", 8.5F),
                Text = "正在加载预览…",
                TextAlign = ContentAlignment.MiddleCenter,
            };
            _surface.Controls.Add(_status);
        }
    }

    private void EnsureSurfaceVisible()
    {
        if (_surface == null || _surface.IsDisposed) return;
        _surface.Show();
        _surface.BringToFront();
        _status?.BringToFront();
        EnsureInputFilter();
    }

    private void StartSession()
    {
        if (_surface == null || _service != null || _disposed) return;

        try
        {
            _service = new RdpClientService(_surface);
            _service.Connected += Service_Connected;
            _service.LoginCompleted += Service_LoginCompleted;
            _service.Disconnected += Service_Disconnected;
            _service.SetPreviewMode(true);
            EnsureInputFilter();
            var previewSettings = _settings.Clone();
            previewSettings.ScreenMode = Configuration.Enums.ScreenMode.Windowed;
            previewSettings.UseAllMonitors = false;
            previewSettings.AutoMaximizeWindow = false;
            previewSettings.AutoResize = false;
            previewSettings.SmartSizing = true;
            previewSettings.Width = 1280;
            previewSettings.Height = 720;
            previewSettings.RedirectClipboard = false;
            previewSettings.RedirectAudioCapture = false;
            previewSettings.AudioPlaybackMode = Configuration.Enums.AudioPlaybackMode.DoNotPlay;
            previewSettings.RedirectDrives = string.Empty;
            previewSettings.RedirectCameras = string.Empty;
            previewSettings.RedirectPrinters = false;
            previewSettings.RedirectComPorts = false;
            previewSettings.RedirectPnpDevices = string.Empty;
            previewSettings.RedirectLocation = false;
            previewSettings.RedirectWebAuthn = false;
            _service.Connect(previewSettings);
        }
        catch (Exception)
        {
            ShowStatus("预览连接失败");
        }
    }

    private void EnsureInputFilter()
    {
        if (_filterInstalled || _surface is not { IsHandleCreated: true } || _disposed) return;
        Application.AddMessageFilter(this);
        _filterInstalled = true;
    }

    private void RemoveInputFilter()
    {
        if (!_filterInstalled) return;
        Application.RemoveMessageFilter(this);
        _filterInstalled = false;
    }

    public bool PreFilterMessage(ref Message m)
    {
        if (_disposed || _surface is not { IsHandleCreated: true }) return false;
        if (m.HWnd != _surface.Handle && !WindowChromeInterop.IsChild(_surface.Handle, m.HWnd))
            return false;

        var message = m.Msg;
        var isMouse = message >= 0x0200 && message <= 0x020D;
        var isKeyboard = message >= 0x0100 && message <= 0x0109;
        return isMouse || isKeyboard;
    }

    private void Service_Connected(object? sender, EventArgs e)
        => HideStatus();

    private void Service_LoginCompleted(object? sender, EventArgs e)
        => HideStatus();

    private void Service_Disconnected(object? sender, Events.DisconnectedEventArgs e)
    {
        if (!_disposed)
            ShowStatus("预览已断开");
    }

    private void HideStatus()
    {
        if (_status is { IsDisposed: false })
            _status.Visible = false;
    }

    private void ShowStatus(string message)
    {
        if (_status is not { IsDisposed: false }) return;
        _status.Text = message;
        _status.Visible = true;
        _status.BringToFront();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveInputFilter();
        if (_service != null)
        {
            _service.Connected -= Service_Connected;
            _service.LoginCompleted -= Service_LoginCompleted;
            _service.Disconnected -= Service_Disconnected;
            _service.Dispose();
            _service = null;
        }
        if (_surface != null)
        {
            if (_surface.Parent != null)
                _surface.Parent.Controls.Remove(_surface);
            _surface.Dispose();
            _surface = null;
        }
        _status = null;
        _viewport = null;
    }
}
