using AxMSTSCLib;
using PureRDP.Configuration;
using PureRDP.Configuration.Enums;
using PureRDP.Events;
using PureRDP.Extensions;
using PureRDP.Models;
using PureRDP.Platform;
using MSTSCLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PureRDP.Services;

internal sealed class RdpClientService : IDisposable
{
    private const uint DEVICE_SCALE_FACTOR = 100;
    private const string RDP_CONTROL_NAME = "rdpControl";
    private const string DESKTOP_SCALE_FACTOR_PROPERTY = "DesktopScaleFactor";
    private const string DEVICE_SCALE_FACTOR_PROPERTY = "DeviceScaleFactor";

    private readonly SemaphoreSlim _connectionSemaphore = new(1, 1);
    private Control _container;
    private ConnectionSettings? _connectionSettings;
    private AxMsRdpClient10NotSafeForScripting? _rdpControl;
    private MsRdpClient10NotSafeForScripting? _rdpClient;
    private bool _fullScreen;
    private readonly System.Windows.Forms.Timer _displayTimer = new() { Interval = 500 };
    private bool _loginComplete;
    private bool _previewMode;
    private KeyboardMode _keyboardHookMode = KeyboardMode.OnLocalComputer;
    private bool _keyboardHookModeApplied;
    private int _displayAttempts;
    private System.Drawing.Size _displayTarget;
    private string? _lastDisplayDiagnostic;
    private string? _connectionLogId;
    private readonly System.Windows.Forms.Timer _connectionQualityTimer = new() { Interval = 3000 };
    private readonly Queue<bool> _qualityProbeResults = new();
    private bool _qualityProbeInFlight;
    private Task _qualityProbeTask = Task.CompletedTask;
    private ConnectionQualitySnapshot? _connectionQuality;
    private readonly object _qualityProbeSync = new();
    private CancellationTokenSource _qualityProbeCancellation = new();
    private int _connectionGeneration;
    private int _qualityProbeRunId;
    private int _disposed;
    private IntPtr _suspendedContainerRedrawHandle;
    private IntPtr _suspendedClientRedrawHandle;

    public bool FullScreen { get => _fullScreen; set => SetFullScreen(value, false); }
    public bool IsConnected
    {
        get
        {
            try { return _rdpClient?.Connected == 1; }
            catch (COMException) { return false; }
            catch (InvalidComObjectException) { return false; }
        }
    }
    internal ConnectionQualitySnapshot? ConnectionQuality => _connectionQuality;
    public event EventHandler? LoginCompleted;

    internal void SetPreviewMode(bool enabled)
    {
        if (_previewMode == enabled) return;
        _previewMode = enabled;
        _displayTimer.Stop();
        if (_rdpClient != null)
        {
            try { _rdpClient.AdvancedSettings9.SmartSizing = enabled; }
            catch (COMException exception) { ReportDisplay($"preview-sizing HRESULT=0x{exception.HResult:X8}"); }
        }
        if (!enabled) ResizeSessionToContainer();
    }

    internal bool HasActiveControl => _rdpControl != null;
    internal bool IsLoginComplete => _loginComplete;

    /// <summary>
    /// Replaces the temporary preview profile with the real connection profile
    /// when the user promotes the in-card session to an interactive window.
    /// The native RDP control and its network session remain the same.
    /// </summary>
    internal void AdoptConnectionSettings(ConnectionSettings settings)
    {
        _connectionSettings = settings.Clone();
    }

    public event EventHandler? Connected;
    public event EventHandler<DisconnectedEventArgs>? Disconnected;
    public event EventHandler? Minimized;
    internal event EventHandler? LeaveFullScreenRequested;
    public event EventHandler<FullScreenEventArgs>? FullScreenChanged;
    internal event EventHandler? ConnectionQualityChanged;

    public RdpClientService(Control container)
    {
        _container = container;
        _container.ClientSizeChanged += ContainerSizeChanged;
        _displayTimer.Tick += DisplayTimerTick;
        _connectionQualityTimer.Tick += ConnectionQualityTimerTick;
        SetConnectionQualityInterval(GlobalSettingsService.Load().Monitoring.ConnectionQualityIntervalSeconds);
        GlobalSettingsService.ConnectionQualityIntervalChanged += SetConnectionQualityInterval;

        var form = container.FindForm();
        if (form == null)
            throw new ArgumentException("The provided container is not part of a Form.", nameof(container));
    }

    /// <summary>
    /// Selects where Windows key combinations are handled for the current
    /// session. The selection is remembered before the native RDP control is
    /// created and applied immediately when the control already exists.
    /// </summary>
    public void SetKeyboardHookMode(KeyboardMode mode)
    {
        var normalized = mode == KeyboardMode.OnRemoteComputer
            ? KeyboardMode.OnRemoteComputer
            : KeyboardMode.OnLocalComputer;
        if (_keyboardHookMode == normalized &&
            (_rdpClient == null || _keyboardHookModeApplied))
            return;

        _keyboardHookMode = normalized;
        if (_rdpClient == null)
        {
            _keyboardHookModeApplied = false;
            return;
        }

        _rdpClient.SecuredSettings3.KeyboardHookMode = (int)_keyboardHookMode;
        _keyboardHookModeApplied = true;
    }

    public void Connect(ConnectionSettings connectionSettings)
    {
        ArgumentNullException.ThrowIfNull(connectionSettings);
        Interlocked.Increment(ref _connectionGeneration);
        Interlocked.Increment(ref _qualityProbeRunId);
        if (_rdpControl != null)
            Disconnect(true);

        _connectionSettings = connectionSettings.Clone();

        SetupConnection();

        var fullScreen = _connectionSettings.ScreenMode == ScreenMode.FullScreen;
        var useAllMonitors = _connectionSettings.UseAllMonitors == true;
        // A tiled session is hosted inside a fixed grid slot.  Do not let a
        // per-device fullscreen preference take over while the grid is being
        // assembled; that race used to leave the new layout covered by a
        // borderless fullscreen window until the next reconnect.
        if (!_tileMode && (fullScreen || useAllMonitors))
            SetFullScreen(true, useAllMonitors);

        _rdpControl.Connect();
    }

    public void Reconnect(ConnectionSettings? connectionSettings = null)
    {
        if (_rdpControl == null || _rdpClient == null || _connectionSettings == null)
            return;

        var updatedSettings = connectionSettings ?? _connectionSettings;

        var sessionCanBeUpdated = updatedSettings
            .EqualsExceptBy(
                _connectionSettings,
                nameof(ConnectionSettings.Width),
                nameof(ConnectionSettings.Height),
                nameof(ConnectionSettings.ScaleFactor),
                nameof(ConnectionSettings.TileScaleFactor),
                nameof(ConnectionSettings.ViewScaleFactor),
                nameof(ConnectionSettings.AutoResize),
                nameof(ConnectionSettings.AutoMaximizeWindow),
                nameof(ConnectionSettings.ScreenMode),
                nameof(ConnectionSettings.KeyboardModeWindowed),
                nameof(ConnectionSettings.KeyboardModeWindowFullscreen),
                nameof(ConnectionSettings.KeyboardModeFullscreen),
                nameof(ConnectionSettings.BadgeColor),
                nameof(ConnectionSettings.DisplayName),
                nameof(ConnectionSettings.LastConnected)
             );

        if (sessionCanBeUpdated)
            UpdateSessionDisplaySettings(updatedSettings);
        else
            Connect(updatedSettings);
    }

    /// <summary>
    /// Updates the remote desktop to the current host window client area after
    /// the user resizes a borderless session window.
    /// </summary>
    public void ResizeSessionToContainer()
    {
        if (_rdpClient == null || _connectionSettings == null)
            return;

        // Paint the current desktop into the new client area immediately.
        // The remote display-size request below is asynchronous, so waiting
        // for its response would otherwise leave the old ActiveX viewport
        // enlarged and clipped for a short period after a maximize click.
        RefreshPictureLayout();

        // Coalesce drag/layout notifications. Renegotiate once the pointer
        // settles instead of resetting the remote display on every pixel.
        ContainerSizeChanged(this, EventArgs.Empty);
    }

    /// <summary>
    /// Recalculates only the local viewport. This is intentionally separate
    /// from remote resolution negotiation so interactive window dragging can
    /// repaint smoothly without sending a display update for every mouse move.
    /// </summary>
    internal void RefreshPictureLayout()
    {
        if (_rdpControl == null || _rdpClient == null || _previewMode || _displayUpdatesSuspended)
            return;

        var width = _rdpClient.DesktopWidth > 0 ? _rdpClient.DesktopWidth : _viewDesktop.Width;
        var height = _rdpClient.DesktopHeight > 0 ? _rdpClient.DesktopHeight : _viewDesktop.Height;
        if (width > 0 && height > 0)
            LayoutPicture((uint)width, (uint)height);
    }

    private bool _tileMode;
    private bool _interactiveResize;
    private bool _displayUpdatesSuspended;
    internal void SetInteractiveResize(bool active)
    {
        if (_interactiveResize == active) return;
        _interactiveResize = active;
        _displayTimer.Stop();
        if (!active) ContainerSizeChanged(this, EventArgs.Empty);
    }

    /// <summary>
    /// Temporarily stops display-layout work while the host form is being
    /// minimized or restored together with its sibling session windows.
    /// WinForms raises several intermediate ClientSizeChanged events during
    /// that transition; processing them would repaint the ActiveX surface
    /// repeatedly and make a batch restore visibly flash.
    /// </summary>
    internal void SetDisplayUpdatesSuspended(bool suspended, bool refreshOnResume = true)
    {
        if (_displayUpdatesSuspended == suspended)
            return;

        _displayUpdatesSuspended = suspended;
        if (suspended)
        {
            _displayTimer.Stop();
            return;
        }

        if (refreshOnResume && _loginComplete && !_previewMode)
            ResizeSessionToContainer();
    }

    internal bool DisplayUpdatesSuspended => _displayUpdatesSuspended;

    internal void ResumeDisplayAfterWindowRestore()
        => SetDisplayUpdatesSuspended(false, refreshOnResume: false);

    internal void SetNativeRedrawEnabled(bool enabled)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (!enabled)
        {
            // A hidden canvas must stay hidden. Resume only HWNDs which this
            // operation actually suspended, and never a recreated HWND.
            if (_suspendedClientRedrawHandle == IntPtr.Zero && _rdpControl is { IsHandleCreated: true } &&
                WindowChromeInterop.IsWindowVisible(_rdpControl.Handle))
            {
                _suspendedClientRedrawHandle = _rdpControl.Handle;
                WindowChromeInterop.SendMessage(_rdpControl.Handle, Interop.WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            }
            if (_suspendedContainerRedrawHandle == IntPtr.Zero && _container.IsHandleCreated &&
                WindowChromeInterop.IsWindowVisible(_container.Handle))
            {
                _suspendedContainerRedrawHandle = _container.Handle;
                WindowChromeInterop.SendMessage(_container.Handle, Interop.WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            }
            return;
        }

        var containerHandle = _suspendedContainerRedrawHandle;
        var clientHandle = _suspendedClientRedrawHandle;
        _suspendedContainerRedrawHandle = IntPtr.Zero;
        _suspendedClientRedrawHandle = IntPtr.Zero;
        if (containerHandle != IntPtr.Zero && _container.IsHandleCreated && _container.Handle == containerHandle)
            WindowChromeInterop.SendMessage(containerHandle, Interop.WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
        if (clientHandle != IntPtr.Zero && _rdpControl is { IsHandleCreated: true } && _rdpControl.Handle == clientHandle)
        {
            WindowChromeInterop.SendMessage(clientHandle, Interop.WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
            _rdpControl.Invalidate(false);
        }
    }

    private uint EffectiveScale => _connectionSettings?.ScaleFactor is uint scale && scale >= 100 ? scale : _container.DeviceZoom;
    private System.Drawing.Size _tileDesktop;
    internal System.Drawing.Size TileDesktopSize => _tileDesktop;
    private System.Drawing.Size _viewDesktop;
    internal System.Drawing.Size PictureDesktopSize => _viewDesktop;

    private void LayoutPicture(uint width, uint height)
    {
        if (_rdpControl == null || _rdpClient == null || _previewMode) return;
        if (width == 0 || height == 0 || _container.ClientSize.Width <= 0 || _container.ClientSize.Height <= 0) return;
        var scale = _tileMode ? _connectionSettings?.TileScaleFactor ?? 0 : _connectionSettings?.ViewScaleFactor ?? 0;
        // A maximized host must remain filled; fixed picture percentages apply
        // to normal windows and tiles, not the working-area maximize mode.
        if (!_tileMode && (_container.FindForm()?.WindowState == FormWindowState.Maximized || _fullScreen)) scale = 0;
        var fitRatio = Math.Min((double)_container.ClientSize.Width / width,
            (double)_container.ClientSize.Height / height);
        var ratio = scale == 0 ? fitRatio : Math.Min(scale / 100.0, fitRatio);
        var size = new System.Drawing.Size(Math.Max(1, (int)Math.Floor(width * ratio)),
            Math.Max(1, (int)Math.Floor(height * ratio)));
        var bounds = new System.Drawing.Rectangle(0, 0, size.Width, size.Height);
        if (_rdpControl.Dock != DockStyle.None) _rdpControl.Dock = DockStyle.None;
        if (_rdpControl.Bounds != bounds) _rdpControl.Bounds = bounds;
        try
        {
            // Always let the RDP control scale the complete desktop into the
            // calculated bounds.  ZoomLevel is not implemented by every
            // Windows RDP ActiveX version; setting it while the window is
            // maximized leaves SmartSizing disabled and enlarges/crops the
            // old desktop during the asynchronous resolution update.
            // SmartSizing also keeps the full picture visible while the
            // server is transitioning between the normal and maximized sizes.
            if (!_rdpClient.AdvancedSettings9.SmartSizing)
                _rdpClient.AdvancedSettings9.SmartSizing = true;
        }
        catch (COMException exception) { ReportDisplay($"picture-scale HRESULT=0x{exception.HResult:X8}"); }
    }

    internal void SetTileMode(bool enabled)
    {
        if (enabled && _fullScreen)
            SetFullScreen(false, true);
        if (enabled && !_tileMode)
            _tileDesktop = new System.Drawing.Size(_connectionSettings?.Width ?? (_rdpClient?.DesktopWidth > 0 ? _rdpClient.DesktopWidth : 1600),
                _connectionSettings?.Height ?? (_rdpClient?.DesktopHeight > 0 ? _rdpClient.DesktopHeight : 900));
        _tileMode = enabled;
        if (_rdpClient != null)
        {
            try { _rdpClient.AdvancedSettings9.SmartSizing = enabled || _previewMode || _connectionSettings?.SmartSizing == true; }
            catch (COMException exception) { ReportDisplay($"tile-sizing HRESULT=0x{exception.HResult:X8}"); }
        }
        ResizeSessionToContainer();
    }

    internal void MoveContainer(Control newContainer)
    {
        ArgumentNullException.ThrowIfNull(newContainer);
        if (ReferenceEquals(_container, newContainer)) return;
        _container.ClientSizeChanged -= ContainerSizeChanged;
        if (_rdpControl != null && _rdpControl.Parent == _container)
            _container.Controls.Remove(_rdpControl);
        _container = newContainer;
        _container.ClientSizeChanged += ContainerSizeChanged;
        if (_rdpControl != null && _rdpControl.Parent != newContainer)
        {
            newContainer.Controls.Add(_rdpControl);
            _rdpControl.Dock = DockStyle.Fill;
        }
    }

    /// <summary>
    /// Returns keyboard focus to the native RDP surface after the custom
    /// window chrome has been shown. This keeps Windows key combinations and
    /// normal keyboard input working immediately after login.
    /// </summary>
    public void FocusRemoteClient()
    {
        if (_rdpControl is not { IsDisposed: false } || _rdpControl.ContainsFocus)
            return;

        // The native RDP surface keeps its child focus when the user returns
        // from another local window. Re-selecting an already focused ActiveX
        // control can make Windows play the default notification sound, and
        // also causes an unnecessary focus handoff during Alt+Tab/click-back.
        // Only request focus when the remote surface actually needs it.
        _rdpControl.Focus();
    }

    public void Disconnect()
        => Disconnect(false);

    private void Disconnect(bool isReconnect)
    {
        _rdpControl?.Disconnect();
        CleanupConnection(DisconnectReasonCode.LocalNotError, DisconnectReasonCodeExt.NoInfo, isReconnect);
    }

    [MemberNotNull(nameof(_rdpControl), nameof(_rdpClient))]
    private void SetupConnection()
    {
        _connectionSemaphore.Wait();

        try
        {
            if (_rdpControl != null)
                throw new InvalidOperationException("RDP connection is already established.");

            CreateRdpControlAndClient();
            WireRdpControlEvents();
            ApplySettingsToRdpClient();
        }
        finally
        {
            _connectionSemaphore.Release();
        }
    }

    private void CleanupConnection(DisconnectReasonCode reasonCode, DisconnectReasonCodeExt extendedErrorCode, bool isReconnect)
    {
        var wasFullScreen = _fullScreen;
        var wasMultiMonitor = _connectionSettings?.UseAllMonitors == true;
        Interlocked.Increment(ref _connectionGeneration);
        CancellationTokenSource qualityCancellation;
        var qualityProbeWasInFlight = false;
        lock (_qualityProbeSync)
        {
            qualityCancellation = _qualityProbeCancellation;
            _qualityProbeCancellation = new CancellationTokenSource();
            Interlocked.Increment(ref _qualityProbeRunId);
            qualityProbeWasInFlight = _qualityProbeInFlight;
            _qualityProbeInFlight = false;
        }
        try { qualityCancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        if (!qualityProbeWasInFlight)
            qualityCancellation.Dispose();

        CompleteConnectionLog();
        _displayTimer.Stop();
        _connectionQualityTimer.Stop();
        _qualityProbeResults.Clear();
        SetConnectionQuality(null);
        _loginComplete = false;
        _connectionSemaphore.Wait();

        string? reason = null;

        try
        {
            if (_rdpControl == null)
            {
                CompleteConnectionLog();
                return;
            }

            try { reason = GetErrorDescription((int)reasonCode); }
            catch (COMException) { }
            catch (InvalidComObjectException) { }
            try { SetFullScreen(false, true); }
            catch (COMException) { }
            catch (InvalidOperationException) { }
            UnwireRdpControlEvents();
            RemoveRdpControlAndClient();
            _keyboardHookModeApplied = false;
        }
        finally
        {
            _connectionSemaphore.Release();
        }

        CompleteConnectionLog();

        Disconnected?.Invoke(this, new DisconnectedEventArgs(
            reasonCode, extendedErrorCode, reason, isReconnect, wasFullScreen, wasMultiMonitor));
    }

    [MemberNotNull(nameof(_rdpControl), nameof(_rdpClient))]
    private void CreateRdpControlAndClient()
    {
        Debug.Assert(_rdpControl is null);
        Debug.Assert(_rdpClient is null);

        _rdpControl?.Dispose();
        _rdpControl = new RdpClientWindowHost();

        _rdpControl.BeginInit();
        _rdpControl.Name = RDP_CONTROL_NAME;
        _rdpControl.Dock = DockStyle.Fill;
        _rdpControl.EndInit();

        _container.Controls.Add(_rdpControl);

        var rdpClient = (MsRdpClient10NotSafeForScripting?)_rdpControl.GetOcx();
        _rdpClient = rdpClient ?? throw new InvalidOperationException("Failed to create RDP client control.");
        _rdpClient.AdvancedSettings9.ContainerHandledFullScreen = 1;
        DisableNativeConnectionBar();
    }

    private void RemoveRdpControlAndClient()
    {
        Debug.Assert(_rdpControl is not null);
        Debug.Assert(_rdpClient is not null);

        var control = _rdpControl;
        var client = _rdpClient;
        _container.Controls.Remove(control);

        try
        {
            control.Dispose();
        }
        finally
        {
            // GetOcx() creates a separate managed reference to the native RDP
            // object. AxHost.Dispose releases its own reference, but leaving
            // this RCW alive keeps native session buffers attached to the
            // long-lived console process after a connection window closes.
            ReleaseComReference(client);
            _rdpControl = null;
            _rdpClient = null;
        }
    }

    private static void ReleaseComReference(object? value)
    {
        if (value == null || !Marshal.IsComObject(value)) return;
        try { Marshal.ReleaseComObject(value); }
        catch (COMException) { }
        catch (InvalidComObjectException) { }
    }

    private void WireRdpControlEvents()
    {
        Debug.Assert(_rdpControl is not null);

        _rdpControl.OnConnected += OnConnected;
        _rdpControl.OnDisconnected += OnDisconnected;
        _rdpControl.OnLoginComplete += OnLoginComplete;
        _rdpControl.OnConfirmClose += OnConfirmClose;
        _rdpControl.OnRequestContainerMinimize += OnRequestContainerMinimize;
        _rdpControl.OnRequestLeaveFullScreen += OnRequestLeaveFullScreen;
        _rdpControl.OnNetworkStatusChanged += OnNetworkStatusChanged;
    }

    private void UnwireRdpControlEvents()
    {
        Debug.Assert(_rdpControl is not null);

        _rdpControl.OnConnected -= OnConnected;
        _rdpControl.OnDisconnected -= OnDisconnected;
        _rdpControl.OnLoginComplete -= OnLoginComplete;
        _rdpControl.OnConfirmClose -= OnConfirmClose;
        _rdpControl.OnRequestContainerMinimize -= OnRequestContainerMinimize;
        _rdpControl.OnRequestLeaveFullScreen -= OnRequestLeaveFullScreen;
        _rdpControl.OnNetworkStatusChanged -= OnNetworkStatusChanged;
    }

    private void ApplySettingsToRdpClient()
    {
        Debug.Assert(_connectionSettings is not null);
        Debug.Assert(_rdpClient is not null);

        var extendedSettings = (IMsRdpExtendedSettings)_rdpClient;

        #region Connection options
        var (host, port) = ParseHostAndPort(_connectionSettings.Hostname);

        _rdpClient.Server = host;

        if (!string.IsNullOrWhiteSpace(port))
            _rdpClient.AdvancedSettings9.RDPPort = int.Parse(port);

        if (_connectionSettings.Domain != null)
            _rdpClient.Domain = _connectionSettings.Domain;

        if (_connectionSettings.Username != null)
            _rdpClient.UserName = _connectionSettings.Username;

        var password = _connectionSettings.Password ?? RdpCredentialService.ReadConnectionPassword(
            _connectionSettings.Hostname,
            _connectionSettings.Username.CombineUsernameAndDomain(_connectionSettings.Domain));
        if (password != null)
            _rdpClient.AdvancedSettings9.ClearTextPassword = password;

        if (_connectionSettings.EnableCredSsp != null)
            _rdpClient.AdvancedSettings9.EnableCredSspSupport = _connectionSettings.EnableCredSsp.Value;

        if (_connectionSettings.AuthenticationLevel != null && _connectionSettings.AuthenticationLevel != AuthenticationLevel.NoRequirement)
            _rdpClient.AdvancedSettings9.AuthenticationLevel = (uint)_connectionSettings.AuthenticationLevel.Value;

        if (_connectionSettings.KeepAliveInterval != null)
            _rdpClient.AdvancedSettings2.keepAliveInterval = _connectionSettings.KeepAliveInterval.Value;

        if (_connectionSettings.ConnectionTimeout != null)
        {
            _rdpClient.AdvancedSettings9.overallConnectionTimeout = _connectionSettings.ConnectionTimeout.Value;
            _rdpClient.AdvancedSettings9.singleConnectionTimeout = _connectionSettings.ConnectionTimeout.Value;
        }

        if (_connectionSettings.MaxReconnectAttempts != null)
            _rdpClient.AdvancedSettings9.MaxReconnectAttempts = _connectionSettings.MaxReconnectAttempts.Value;

        // Reconnection is coordinated by PureRDP so the user gets a consistent
        // retry schedule and can cancel by closing this session window. Leaving
        // ActiveX auto-reconnect enabled creates its own native popup and retry loop.
        _rdpClient.AdvancedSettings9.EnableAutoReconnect = false;
        #endregion Connection options

        #region Display options
        // A windowed auto-maximized session must start at the actual client
        // area size. Using an older profile resolution here makes the RDP
        // ActiveX control create its own horizontal and vertical scrollbars
        // before dynamic resolution has a chance to update the session.
        var form = _container.FindForm();
        var autoSizeToContainer = !_previewMode && _connectionSettings.ScreenMode != ScreenMode.FullScreen &&
                                  _connectionSettings.UseAllMonitors != true &&
                                  (_connectionSettings.AutoMaximizeWindow == true ||
                                   _connectionSettings.AutoResize != false ||
                                   form?.WindowState == FormWindowState.Maximized);
        var fillWindow = !_tileMode && (form?.WindowState == FormWindowState.Maximized || _fullScreen || _connectionSettings.AutoMaximizeWindow == true);
        var initialWidth = fillWindow || autoSizeToContainer && !_connectionSettings.Width.HasValue
            ? GetRemoteDimension(_container.ClientSize.Width)
            : GetRemoteDimension(_connectionSettings.Width ?? _container.ClientSize.Width);
        var initialHeight = fillWindow || autoSizeToContainer && !_connectionSettings.Height.HasValue
            ? GetRemoteDimension(_container.ClientSize.Height)
            : GetRemoteDimension(_connectionSettings.Height ?? _container.ClientSize.Height);
        if (initialWidth > 0)
            _rdpClient.DesktopWidth = (int)initialWidth;
        if (initialHeight > 0)
            _rdpClient.DesktopHeight = (int)initialHeight;
        _viewDesktop = new System.Drawing.Size((int)initialWidth, (int)initialHeight);

        if (_connectionSettings.ColorDepth != null)
            _rdpClient.ColorDepth = _connectionSettings.ColorDepth.Value;

        var scaleFactor = EffectiveScale;
        if (scaleFactor == 0) scaleFactor = _container.DeviceZoom;
        extendedSettings.set_Property(DESKTOP_SCALE_FACTOR_PROPERTY, Math.Max(100u, scaleFactor));
        extendedSettings.set_Property(DEVICE_SCALE_FACTOR_PROPERTY, DEVICE_SCALE_FACTOR);

        if (_previewMode || _tileMode || _connectionSettings.Width.HasValue || _connectionSettings.Height.HasValue)
            _rdpClient.AdvancedSettings9.SmartSizing = true;
        else if (autoSizeToContainer)
            _rdpClient.AdvancedSettings9.SmartSizing = false;
        else if (_connectionSettings.SmartSizing != null)
            _rdpClient.AdvancedSettings9.SmartSizing = _connectionSettings.SmartSizing.Value;

        ((IMsRdpClientNonScriptable5)_rdpClient).UseMultimon = _connectionSettings.UseAllMonitors == true;
        LayoutPicture(initialWidth, initialHeight);

        if (_connectionSettings.EnableCompression != null)
            _rdpClient.AdvancedSettings9.Compress = _connectionSettings.EnableCompression.Value ? 1 : 0;

        if (_connectionSettings.EnableBitmapPersistence != null)
            _rdpClient.AdvancedSettings9.BitmapPersistence = _connectionSettings.EnableBitmapPersistence.Value ? 1 : 0;
        #endregion Display options

        #region Redirection options
        if (_connectionSettings.RedirectAudioCapture != null)
            _rdpClient.AdvancedSettings9.AudioCaptureRedirectionMode = _connectionSettings.RedirectAudioCapture.Value;

        if (_connectionSettings.AudioPlaybackMode != null)
            _rdpClient.SecuredSettings3.AudioRedirectionMode = (int)_connectionSettings.AudioPlaybackMode.Value;

        if (_connectionSettings.RedirectCameras != null)
            ApplyCameraRedirection(_connectionSettings.RedirectCameras);

        if (_connectionSettings.RedirectClipboard != null)
            _rdpClient.AdvancedSettings9.RedirectClipboard = _connectionSettings.RedirectClipboard.Value;

        if (_connectionSettings.RedirectComPorts != null)
            _rdpClient.AdvancedSettings9.RedirectPorts = _connectionSettings.RedirectComPorts.Value;

        if (_connectionSettings.RedirectPnpDevices != null)
            ApplyPnpDeviceRedirection(_connectionSettings.RedirectPnpDevices);

        if (_connectionSettings.RedirectPrinters != null)
            _rdpClient.AdvancedSettings9.RedirectPrinters = _connectionSettings.RedirectPrinters.Value;

        if (_connectionSettings.RedirectDrives != null)
            ApplyDriveRedirection(_connectionSettings.RedirectDrives);

        if (_connectionSettings.RedirectLocation != null)
        {
            object redirectLocation = _connectionSettings.RedirectLocation.Value;
            extendedSettings.set_Property("EnableLocationRedirection", ref redirectLocation);
        }

        if (_connectionSettings.RedirectSmartCards != null)
            _rdpClient.AdvancedSettings9.RedirectSmartCards = _connectionSettings.RedirectSmartCards.Value;

        if (_connectionSettings.RedirectWebAuthn != null)
            _rdpClient.AdvancedSettings9.PluginDlls = "WebAuthn.dll";

        // Apply the mode selected by the host window. The host keeps this
        // value separate from the connection profile so it can change when
        // the window moves between ordinary, maximized and true fullscreen.
        _rdpClient.SecuredSettings3.KeyboardHookMode = (int)_keyboardHookMode;
        _keyboardHookModeApplied = true;
        #endregion

        #region Experience options
        // PureRDP supplies its own compact glass control strip. The native
        // MSTSC connection bar must be disabled before Connect(), otherwise it
        // is created by the ActiveX control as soon as a full-screen session
        // starts and covers the custom controls.
        DisableNativeConnectionBar();
        #endregion Experience options
    }

    private static (string Host, string Port) ParseHostAndPort(string? hostname)
    {
        var hostPortMatch = Regex.Match(hostname ?? string.Empty, @"^(?<host>(?:\[([0-9a-fA-F:]+)\])|([^:\[\]]+))(?::(?<port>\d+))?$");
        if (!hostPortMatch.Success)
            throw new ArgumentException("Invalid hostname / IP address format.", nameof(hostname));

        var host = hostPortMatch.Groups["host"].Value;
        // MSTSC defaults to TCP 3389, but assigning it explicitly keeps
        // typed endpoints and endpoints without a suffix on the same path.
        var port = hostPortMatch.Groups["port"].Success
            ? hostPortMatch.Groups["port"].Value
            : "3389";
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("No hostname / IP address specified.", nameof(hostname));

        return (host, port);
    }

    private void ApplyCameraRedirection(string camerasToRedirect)
    {
        Debug.Assert(_rdpClient is not null);

        // ReSharper disable once SuspiciousTypeConversion.Global
        var cameraCollection = ((IMsRdpClientNonScriptable8)_rdpClient).CameraRedirConfigCollection;

        if (camerasToRedirect == "*")
        {
            for (uint i = 0; i < cameraCollection.Count; i++)
                cameraCollection.ByIndex[i].Redirected = true;
        }
        else
        {
            var requestedCameras = camerasToRedirect
                .Split([';', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(camera => camera.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            for (uint i = 0; i < cameraCollection.Count; i++)
            {
                var camera = cameraCollection.ByIndex[i];
                camera.Redirected = requestedCameras.Contains(camera.SymbolicLink);
            }
        }
    }

    private void ApplyPnpDeviceRedirection(string devicesToRedirect)
    {
        Debug.Assert(_rdpClient is not null);

        var deviceCollection = ((IMsRdpClientNonScriptable3)_rdpClient).DeviceCollection;

        if (devicesToRedirect.Equals("DynamicDevices", StringComparison.OrdinalIgnoreCase))
        {
            _rdpClient.AdvancedSettings6.RedirectDevices = true;
        }
        else if (devicesToRedirect == "*")
        {
            for (uint i = 0; i < deviceCollection.DeviceCount; i++)
                deviceCollection.DeviceByIndex[i].RedirectionState = true;
        }
        else
        {
            var requestedDevices = devicesToRedirect
                .Split([';', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(camera => camera.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            for (uint i = 0; i < deviceCollection.DeviceCount; i++)
            {
                var device = deviceCollection.DeviceByIndex[i];
                var isRedirected = requestedDevices.Contains(device.FriendlyName) || requestedDevices.Contains(device.DeviceInstanceId);
                device.RedirectionState = isRedirected;
            }
        }
    }

    private void ApplyDriveRedirection(string drivesToRedirect)
    {
        Debug.Assert(_rdpClient is not null);

        var driveCollection = ((IMsRdpClientNonScriptable3)_rdpClient).DriveCollection;

        if (drivesToRedirect.Equals("DynamicDrives", StringComparison.OrdinalIgnoreCase))
        {
            _rdpClient.AdvancedSettings9.RedirectDrives = true;
        }
        else if (drivesToRedirect == "*")
        {
            for (uint i = 0; i < driveCollection.DriveCount; i++)
                driveCollection.DriveByIndex[i].RedirectionState = true;
        }
        else
        {
            var requestedDrives = drivesToRedirect
                .Split([';', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(drive => drive.Trim().TrimEnd('\\', ':'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            for (uint i = 0; i < driveCollection.DriveCount; i++)
            {
                var drive = driveCollection.DriveByIndex[i];
                var driveLetter = drive.Name[..1];
                drive.RedirectionState = requestedDrives.Contains(driveLetter);
            }
        }
    }

    private void UpdateSessionDisplaySettings(ConnectionSettings connectionSettings, bool forceContainerSize = false)
    {
        if (_previewMode) return;
        if (_interactiveResize)
        {
            RefreshPictureLayout();
            return;
        }
        Debug.Assert(_rdpClient is not null);

        _connectionSettings = connectionSettings.Clone();

        var form = _container.FindForm();
        var disconnected = !_loginComplete || _rdpClient.Connected != 1;
        var useAllMonitors = _connectionSettings.UseAllMonitors == true;
        if (disconnected || useAllMonitors)
            return;

        var currentWidth = (uint)_rdpClient.DesktopWidth;
        var currentHeight = (uint)_rdpClient.DesktopHeight;
        var fillWindow = !_tileMode && (form?.WindowState == FormWindowState.Maximized || _fullScreen);
        // Width/height are the initial fixed desktop size when dynamic
        // resolution is disabled. Once AutoResize is enabled, they remain
        // useful as the initial size but resizing the ordinary window must
        // negotiate the actual client area; otherwise the old desktop stays
        // centered in a larger host and exposes the default background frame.
        var resizeToContainer = fillWindow || _connectionSettings.AutoResize == true ||
                                _connectionSettings.AutoMaximizeWindow == true ||
                                forceContainerSize && !_connectionSettings.Width.HasValue && !_connectionSettings.Height.HasValue;
        var updatedWidth = resizeToContainer
            ? GetRemoteDimension(_container.ClientSize.Width)
            : GetRemoteDimension(_connectionSettings.Width ?? _container.ClientSize.Width);
        var updatedHeight = resizeToContainer
            ? GetRemoteDimension(_container.ClientSize.Height)
            : GetRemoteDimension(_connectionSettings.Height ?? _container.ClientSize.Height);

        if (updatedWidth < 200 || updatedHeight < 200 ||
            form?.WindowState == FormWindowState.Minimized)
            return;

        var extendedSettings = (IMsRdpExtendedSettings)_rdpClient;
        var currentScaleFactor = (uint)extendedSettings.get_Property(DESKTOP_SCALE_FACTOR_PROPERTY);
        var updatedScaleFactor = EffectiveScale;
        if (updatedScaleFactor == 0) updatedScaleFactor = _container.DeviceZoom;
        if (_tileMode)
        {
            updatedWidth = GetRemoteDimension(_connectionSettings.Width ?? _tileDesktop.Width);
            updatedHeight = GetRemoteDimension(_connectionSettings.Height ?? _tileDesktop.Height);
        }
        else if (!fillWindow && _connectionSettings.ViewScaleFactor is > 0 and not 100 && _viewDesktop.Width > 0)
        {
            updatedWidth = GetRemoteDimension(_connectionSettings.Width ?? _viewDesktop.Width);
            updatedHeight = GetRemoteDimension(_connectionSettings.Height ?? _viewDesktop.Height);
        }
        updatedScaleFactor = Math.Max(100u, updatedScaleFactor);
        // A display update is asynchronous. Lay out against the requested
        // desktop size so the picture already fits the new window while the
        // server applies the update. SmartSizing keeps the old frame complete
        // during that transition as well.
        LayoutPicture(updatedWidth, updatedHeight);

        if (currentWidth == updatedWidth && currentHeight == updatedHeight && currentScaleFactor == updatedScaleFactor &&
            _rdpClient.HorizontalScrollBarVisible == 0 && _rdpClient.VerticalScrollBarVisible == 0)
        {
            _displayTimer.Stop();
            return;
        }

        try
        {
            if (_rdpClient is IMsRdpClient10 rdpClient10) // RDP 10/8.1+
            {
                // UpdateSessionDisplaySettings changes the existing session in
                // place. Do not follow it with Reconnect: reconnecting from a
                // resize callback raises the disconnected event and can make
                // the session window close while the connection is starting.
                // Physical dimensions are millimetres, not desktop pixels.
                var dpi = Math.Max(96, _container.DeviceDpi);
                var physicalWidth = (uint)Math.Clamp(Math.Round(updatedWidth * 25.4 / dpi), 10, 1800);
                var physicalHeight = (uint)Math.Clamp(Math.Round(updatedHeight * 25.4 / dpi), 10, 1800);
                rdpClient10.UpdateSessionDisplaySettings(updatedWidth, updatedHeight, physicalWidth, physicalHeight, 0, updatedScaleFactor, DEVICE_SCALE_FACTOR);
                ReportDisplay($"requested={updatedWidth}x{updatedHeight}; remote={_rdpClient.DesktopWidth}x{_rdpClient.DesktopHeight}; scroll={_rdpClient.HorizontalScrollBarVisible},{_rdpClient.VerticalScrollBarVisible}");
            }
            else // Fallback: RDP 8
                _rdpClient.Reconnect(updatedWidth, updatedHeight);
        }
        catch (COMException exception)
        {
            ReportDisplay($"requested={updatedWidth}x{updatedHeight}; HRESULT=0x{exception.HResult:X8}; {exception.Message}");
        }
    }

    private void ReportDisplay(string message)
    {
        if (_lastDisplayDiagnostic == message) return;
        _lastDisplayDiagnostic = message;
        ConnectionLogService.WriteDiagnostic(message);
    }

    private void ContainerSizeChanged(object? sender, EventArgs e)
    {
        if (!_loginComplete || _previewMode || _displayUpdatesSuspended) return;
        RefreshPictureLayout();
        _displayTimer.Stop();
        if (_interactiveResize) return;
        _displayAttempts = 0;
        _displayTarget = _container.ClientSize;
        _displayTimer.Start();
    }

    private void DisplayTimerTick(object? sender, EventArgs e)
    {
        if (_interactiveResize || _displayUpdatesSuspended) { _displayTimer.Stop(); return; }
        if (!_loginComplete || _rdpClient == null) { _displayTimer.Stop(); return; }
        if (_displayTarget != _container.ClientSize)
        {
            _displayTarget = _container.ClientSize;
            _displayAttempts = 0;
        }
        try
        {
            if (_connectionSettings != null)
                UpdateSessionDisplaySettings(_connectionSettings, forceContainerSize: true);
        }
        catch (COMException exception) { ReportDisplay($"HRESULT=0x{exception.HResult:X8}"); }
        if (++_displayAttempts >= 10) _displayTimer.Stop();
    }

    private static uint GetRemoteDimension(int dimension)
    {
        if (dimension <= 0)
            return 0;

        // RDP desktop dimensions are negotiated as even values. Rounding
        // down avoids asking the server for one extra pixel that the client
        // control cannot display, which otherwise creates a scrollbar.
        return (uint)Math.Max(2, dimension - dimension % 2);
    }

    private void DisableNativeConnectionBar()
    {
        // These are connection-time settings, not live fullscreen controls.
        // Rewriting them after Connect may throw E_UNEXPECTED.
        if (_rdpClient == null || _rdpClient.Connected != 0) return;
        _rdpClient.AdvancedSettings9.DisplayConnectionBar = false;
        _rdpClient.AdvancedSettings9.PinConnectionBar = false;
        ((IMsRdpClientNonScriptable5)_rdpClient).DisableConnectionBar = true;
    }

    private void SetFullScreen(bool value, bool forceIfDisconnected)
    {
        if (_fullScreen == value)
            return;

        if (_rdpClient == null && !forceIfDisconnected)
            return;

        _fullScreen = value;
        var fullScreenEventArgs = new FullScreenEventArgs(_fullScreen, _connectionSettings?.UseAllMonitors ?? false);
        FullScreenChanged?.Invoke(this, fullScreenEventArgs);
        // The borderless host covers Screen.Bounds (including the taskbar).
        // Keep ActiveX windowed so it does not enter a second, native fullscreen
        // lifecycle when Win+D, Alt+Tab or the local Start menu changes focus.
        ResizeSessionToContainer();
    }

    private string? GetErrorDescription(int reasonCode = 0)
        => _rdpClient?.GetErrorDescription((uint)reasonCode, (uint)_rdpClient.ExtendedDisconnectReason);

    private void OnConnected(object? sender, EventArgs e)
    {
        ReportDisplay($"session={_connectionSettings?.Hostname}; dynamic-display={_rdpClient is IMsRdpClient10}; codec=negotiated-by-RDP/not-exposed; compression={_connectionSettings?.EnableCompression}; bitmap-cache={_connectionSettings?.EnableBitmapPersistence}");
        _qualityProbeResults.Clear();
        SetConnectionQuality(ConnectionQualitySnapshot.Measuring);
        _connectionQualityTimer.Start();
        _ = ProbeConnectionQualityAsync();
        Connected?.Invoke(this, EventArgs.Empty);
    }

    private void OnNetworkStatusChanged(object? sender, IMsTscAxEvents_OnNetworkStatusChangedEvent e)
    {
        var packetLoss = _connectionQuality?.PacketLossPercent;
        SetConnectionQuality(ConnectionQualitySnapshot.FromRdp(
            e.qualityLevel, e.bandwidth, e.rtt, packetLoss));
        ReportDisplay($"network session={_connectionSettings?.Hostname}; estimated-bandwidth-kbps={e.bandwidth}; rtt-ms={e.rtt}; native-quality={e.qualityLevel}");
    }

    private async void ConnectionQualityTimerTick(object? sender, EventArgs e)
    {
        ConnectionLogService.Heartbeat(_connectionLogId);
        await ProbeConnectionQualityAsync();
    }

    internal Task RefreshConnectionQualityAsync() => ProbeConnectionQualityAsync();

    private Task ProbeConnectionQualityAsync()
    {
        lock (_qualityProbeSync)
        {
            if (Volatile.Read(ref _disposed) != 0) return Task.CompletedTask;
            // Manual refresh waits for an existing periodic sample instead of
            // returning early or starting a second probe of the same session.
            if (_qualityProbeInFlight) return _qualityProbeTask;
            return _qualityProbeTask = ProbeConnectionQualityCoreAsync();
        }
    }

    private async Task ProbeConnectionQualityCoreAsync()
    {
        ConnectionSettings settings;
        int generation;
        int runId;
        CancellationToken cancellationToken;
        CancellationTokenSource cancellationSource;
        lock (_qualityProbeSync)
        {
            if (_qualityProbeInFlight || _connectionSettings == null || !IsConnected)
                return;

            settings = _connectionSettings.Clone();
            generation = Volatile.Read(ref _connectionGeneration);
            runId = Interlocked.Increment(ref _qualityProbeRunId);
            cancellationSource = _qualityProbeCancellation;
            cancellationToken = cancellationSource.Token;
            _qualityProbeInFlight = true;
        }
        try
        {
            var probe = await ConnectionQualityProbe.ProbeAsync(settings, cancellationToken);
            if (probe is not ConnectionProbeResult result ||
                generation != Volatile.Read(ref _connectionGeneration) || !IsConnected)
                return;

            _qualityProbeResults.Enqueue(result.Success);
            while (_qualityProbeResults.Count > 20)
                _qualityProbeResults.Dequeue();

            var failed = _qualityProbeResults.Count(item => !item);
            var packetLoss = _qualityProbeResults.Count == 0
                ? 0
                : failed * 100.0 / _qualityProbeResults.Count;
            SetConnectionQuality((_connectionQuality ?? ConnectionQualitySnapshot.Measuring)
                .WithLatency(result.LatencyMs)
                .WithPacketLoss(packetLoss));
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally
        {
            if (runId == Volatile.Read(ref _qualityProbeRunId))
                _qualityProbeInFlight = false;
            if (cancellationSource.IsCancellationRequested)
                cancellationSource.Dispose();
        }
    }

    private void SetConnectionQuality(ConnectionQualitySnapshot? snapshot)
    {
        if (_connectionQuality == snapshot)
            return;

        _connectionQuality = snapshot;
        try { ConnectionQualityChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception) { }
    }

    private void CompleteConnectionLog()
    {
        if (_connectionLogId == null) return;
        ConnectionLogService.Complete(_connectionLogId);
        _connectionLogId = null;
    }

    private void OnDisconnected(object? sender, IMsTscAxEvents_OnDisconnectedEvent e)
    {
        Debug.Assert(_rdpClient is not null);
        CleanupConnection((DisconnectReasonCode)e.discReason, (DisconnectReasonCodeExt)_rdpClient.ExtendedDisconnectReason, false);
    }

    private void OnLoginComplete(object? sender, EventArgs e)
    {
        _loginComplete = true;
        if (_connectionLogId == null && _connectionSettings != null)
            _connectionLogId = ConnectionLogService.Start(_connectionSettings);
        ContainerSizeChanged(sender, e);
        LoginCompleted?.Invoke(this, EventArgs.Empty);
    }

    private static void OnConfirmClose(object? sender, IMsTscAxEvents_OnConfirmCloseEvent e)
        => e.pfAllowClose = true;

    private void OnRequestContainerMinimize(object? sender, EventArgs e)
        => Minimized?.Invoke(this, EventArgs.Empty);

    private void OnRequestLeaveFullScreen(object? sender, EventArgs e)
        => LeaveFullScreenRequested?.Invoke(this, EventArgs.Empty);

    #region IDisposable Support
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _container.ClientSizeChanged -= ContainerSizeChanged;
        _displayTimer.Stop();
        _connectionQualityTimer.Stop();
        try
        {
            // Dispose can be called directly when a session form is closed;
            // that path previously skipped the explicit ActiveX disconnect
            // performed by the normal Disconnect button.
            if (_rdpControl != null)
                UnwireRdpControlEvents();
            try { _rdpControl?.Disconnect(); }
            catch (COMException) { }
            catch (InvalidComObjectException) { }
            CleanupConnection(DisconnectReasonCode.LocalNotError, DisconnectReasonCodeExt.NoInfo, false);
        }
        finally
        {
            _displayTimer.Dispose();
            GlobalSettingsService.ConnectionQualityIntervalChanged -= SetConnectionQualityInterval;
            _connectionQualityTimer.Dispose();
            CancellationTokenSource qualityCancellation;
            var qualityProbeWasInFlight = false;
            lock (_qualityProbeSync)
            {
                qualityCancellation = _qualityProbeCancellation;
                qualityProbeWasInFlight = _qualityProbeInFlight;
                _qualityProbeInFlight = false;
            }
            try { qualityCancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            if (!qualityProbeWasInFlight)
                qualityCancellation.Dispose();
            _connectionSemaphore.Dispose();
            _connectionSettings = null;
            _viewDesktop = System.Drawing.Size.Empty;
            _tileDesktop = System.Drawing.Size.Empty;
            _displayTarget = System.Drawing.Size.Empty;
            _lastDisplayDiagnostic = null;
        }
    }
    private void SetConnectionQualityInterval(int seconds)
    {
        _connectionQualityTimer.Interval = Math.Clamp(seconds <= 0 ? 3 : seconds, 1, 86400) * 1000;
        // Apply a newly saved interval immediately instead of waiting for the
        // old timer schedule to elapse. The in-flight guard prevents duplicate
        // probes when a timer tick is already running.
        if (IsConnected)
            _ = ProbeConnectionQualityAsync();
    }

    #endregion IDisposable Support
}
