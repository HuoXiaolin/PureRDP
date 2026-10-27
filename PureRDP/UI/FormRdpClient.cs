using PureRDP.CommandLine;
using PureRDP.Configuration;
using PureRDP.Configuration.Enums;
using PureRDP.Events;
using PureRDP.Extensions;
using PureRDP.Platform;
using PureRDP.Services;
using PureRDP.UI.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PureRDP.UI;

public partial class FormRdpClient : Form
{
    internal const int ResizeGripSize = 5;
    private const int MaxAutomaticReconnectAttempts = 10;
    private RdpClientService _rdpService;
    private readonly bool _isSessionWindow;
    private readonly FormRdpClient? _managementWindow;
    private bool _previewOnly;
    private ApplicationSettings _appSettings;
    private Rectangle _nonFullScreenBounds;
    private FormWindowState _nonFullScreenState;

    // Ambient services for window message handling
    private readonly FormSizeEnforcementService _formSizeEnforcementService;
    private readonly FormResizeDetectionService _formResizeDetectionService;
    private readonly FormSystemMenuService _formSystemMenuService;

    // Cancellation token source for delayed connection message display
    private readonly CrystalLoaderControl _connectionLoader = new();
    private ConnectionLibraryPanel? _connectionLibraryPanel;
    private GlassWindowControlStrip? _glassChromeStrip;
    private GlassWindowControlStrip[] _cornerGrips = [];
    private bool _settingsConnectRequested;
    private FormRdpClient? _sessionWindow;
    private readonly List<FormRdpClient> _sessionWindows = [];
    private readonly Dictionary<string, PreviewSessionHost> _previewSessions = new(StringComparer.OrdinalIgnoreCase);
    private bool _autoTileSessions;
    private ControlBarPosition _globalControlBarPosition = ControlBarPosition.Right;
    private bool _globalControlBarAutoHide = true;
    private bool _arrangingSessions;
    private bool _idleMemoryCleanupPending;
    private GlobalShortcutPreferences _globalShortcutPreferences = new();
    private GlobalShortcutMessageFilter? _globalShortcutFilter;
    private KeyboardMode? _appliedKeyboardMode;
    private Rectangle? _independentBounds;
    private FormWindowState _independentState;
    private bool _independentFullScreen;
    private GlobalSettingsForm? _globalSettingsWindow;
    private DeviceAvailabilityScanner? _availabilityScanner;
    private bool _launchedFromLibrary;
    private bool _remoteChromeActive;
    private bool _sessionClosing;
    private bool _applyConnectionWindowSizeAfterConnect;
    private bool _remoteResizeQueued;
    private bool _suppressRemoteResizeAfterRestore;
    private bool _restoreRedrawSuspended;
    private bool _restoringControlBarState;
    private bool _suppressNextActivationVisualRefresh;
    private int _manualResizeHitTest = Interop.HTCLIENT;
    private Point _manualResizeStartCursor;
    private Rectangle _manualResizeStartBounds;
    private RemoteResizeInputFilter? _resizeInputFilter;
    private NotifyIcon? _trayIcon;
    private ContextMenuStrip? _trayMenu;
    private bool _allowApplicationExit;
    private bool _suppressInitialConsoleVisibility;
    private readonly bool _startedSilently;
    private bool _fullScreenMultiMonitor;
    private bool _restoreFullScreenOnConnect;
    private bool _adoptedPreviewSession;
    private bool _resourcesDisposed;
    private bool _sessionCloseRecoveryQueued;
    private bool _bulkWindowStateTransition;
    private bool _fullScreenTransitionInProgress;
    private RdpReconnectDialog? _reconnectDialog;
    private int _automaticReconnectAttempts;
    private bool _automaticReconnectActive;
    private bool _automaticReconnectAttemptInProgress;
    private bool _reconnectFullScreen;
    private bool _reconnectFullScreenMultiMonitor;

    private sealed record SessionWindowStateSnapshot(
        FormWindowState WindowState,
        Rectangle Bounds,
        bool FullScreen,
        bool FullScreenMultiMonitor,
        FormWindowState NonFullScreenState,
        Rectangle NonFullScreenBounds);

    protected override bool ShowWithoutActivation => _previewOnly;

    public FormRdpClient(string[] args)
        : this(args, isSessionWindow: false, managementWindow: null)
    {
    }

    private FormRdpClient(string[] args, bool isSessionWindow, FormRdpClient? managementWindow)
        : this(args, isSessionWindow, managementWindow, previewOnly: false)
    {
    }

    private FormRdpClient(string[] args, bool isSessionWindow, FormRdpClient? managementWindow, bool previewOnly)
    {
        _isSessionWindow = isSessionWindow;
        _managementWindow = managementWindow;
        _previewOnly = previewOnly;

        InitializeComponent();
        panelConnect.Controls.Add(_connectionLoader);
        panelConnect.Resize += ConnectingSurface_Resize;
        panelConnect.BackColor = Color.FromArgb(247, 250, 253);
        _connectionLoader.BackColor = panelConnect.BackColor;
        lbConnecting.Font = new Font(UiDesignTokens.FontFamily, 11F);
        lbConnecting.AutoEllipsis = true;
        btnCancel.Font = new Font(UiDesignTokens.FontFamily, 10F);
        LayoutConnectingSurface();
        // Do not expose the default WinForms gray/white surface while the
        // native RDP control is being resized or is waiting for a remote
        // display update. Any unavoidable aspect-ratio edge is rendered as a
        // deliberate canvas color instead of a transient white frame.
        panelRdp.BackColor = Color.Black;
        if (!_isSessionWindow)
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
        }
        InitializeSurfaceVisibility();

        InitializeWindowChrome();

        if (_previewOnly)
        {
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            ControlBox = false;
            MinimizeBox = false;
            MaximizeBox = false;
            var virtualScreen = SystemInformation.VirtualScreen;
            Location = new Point(virtualScreen.Right + 2048, virtualScreen.Top);
            ClientSize = new Size(1280, 720);
        }

        Icon = Properties.Resources.AppIcon;

        _appSettings = CommandLineOptions.Parse(args);
        _startedSilently = !_isSessionWindow && _appSettings.SilentStartup == true;
        _suppressInitialConsoleVisibility = _startedSilently;
        if (!_isSessionWindow)
        {
            var globalSettings = GlobalSettingsService.Load();
            _autoTileSessions = globalSettings.Windows.AutoTileSessions;
            _globalControlBarPosition = globalSettings.Windows.ControlBarDock;
            _globalControlBarAutoHide = globalSettings.Windows.ControlBarAutoHide;
            _globalShortcutPreferences = globalSettings.Shortcuts.Clone();
            _globalShortcutFilter = new GlobalShortcutMessageFilter(this);
            Application.AddMessageFilter(_globalShortcutFilter);
            _availabilityScanner = new DeviceAvailabilityScanner();
            _availabilityScanner.SetInterval(globalSettings.Monitoring.AvailabilityScanIntervalSeconds);
            _availabilityScanner.StatusChanged += AvailabilityScanner_StatusChanged;
            InitializeConnectionLibrary();
            InitializeTrayIcon();
            _availabilityScanner.Start();
        }

        _formSizeEnforcementService = new FormSizeEnforcementService();
        _formSizeEnforcementService.IsActive = _appSettings.Connection.AutoResize == true;

        _formResizeDetectionService = new FormResizeDetectionService();
        _formResizeDetectionService.ResizeDetected += ResizeDetected;

        _formSystemMenuService = new FormSystemMenuService();
        _formSystemMenuService.ConnectRequested += (_, _) => Connect();
        _formSystemMenuService.DisconnectRequested += (_, _) => Disconnect();
        _formSystemMenuService.FullScreenRequested += (_, _) => ToggleFullScreen();
        _formSystemMenuService.SettingsRequested += (_, _) => (_managementWindow ?? this).ShowSettings();
        _formSystemMenuService.ZoomLevelRequested += (_, e) => SetZoomLevel(e.ZoomLevel);

        _rdpService = new RdpClientService(panelRdp);
        if (_isSessionWindow)
        {
            _resizeInputFilter = new RemoteResizeInputFilter(this);
            Application.AddMessageFilter(_resizeInputFilter);
        }
        WireRdpEvents();
        Activated += (_, _) =>
        {
            if (_isSessionWindow && _managementWindow != null && !_previewOnly)
                _managementWindow._sessionWindow = this;
            if (!_isSessionWindow || _previewOnly || !_rdpService.IsConnected || !IsHandleCreated)
                return;
            BeginInvoke(new Action(() =>
            {
                var suppressVisualRefresh = _suppressNextActivationVisualRefresh;
                _suppressNextActivationVisualRefresh = false;
                if (IsDisposed || _sessionClosing || Form.ActiveForm != this ||
                    WindowState == FormWindowState.Minimized)
                    return;
                ApplyKeyboardModeForCurrentWindow();
                RefreshGlobalShortcutHookOrder();
                if (suppressVisualRefresh || (_managementWindow ?? this)._bulkWindowStateTransition)
                    return;
                // Returning to this form from another local window already
                // restores the native RDP child focus. Do not force another
                // ActiveX focus handoff on every activation: that handoff is
                // what produces the Windows notification sound.
                panelRdp.Invalidate(true);
                QueueRemoteResize();
            }));
        };
        SetRemoteConnectionChrome(false);

        if (_isSessionWindow)
        {
            panelStartup.Visible = false;
            panelConnect.Visible = false;
            panelRdp.Visible = true;
            panelRdp.BringToFront();
        }
    }

    private void Connect()
    {
        var settingsAreInvalid = !_appSettings.Connection.IsValid();
        if (settingsAreInvalid)
        {
            if (!ShowSettings() || !_settingsConnectRequested) return;
            settingsAreInvalid = !_appSettings.Connection.IsValid();
        }

        if (settingsAreInvalid)
        {
            ShowConnectionLibrary();
            return;
        }

        if (!_isSessionWindow)
        {
            OpenSessionWindow();
            return;
        }

        StartConnection();
    }

    /// <summary>
    /// Handles a second launch after the single-instance guard has redirected
    /// its command line to this process. An empty command line is the normal
    /// application shortcut and only brings the console to the foreground;
    /// connection arguments follow the same startup path as the first launch.
    /// </summary>
    internal void HandleSingleInstanceCommand(string[] args)
    {
        if (_sessionClosing || IsDisposed)
            return;

        if (args.Length == 0)
        {
            ShowManagementWindow();
            return;
        }

        try
        {
            var incomingSettings = CommandLineOptions.Parse(args);
            // A second login/startup invocation must not wake an existing
            // tray instance or replace its interactive connection settings.
            if (incomingSettings.SilentStartup == true) return;
            _appSettings = incomingSettings;
            ShowManagementWindow();
            AutoStartup();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // CommandLineOptions already reports expected parse errors. Keep
            // the primary console alive if a forwarded shell association is
            // malformed or the target file is temporarily unavailable.
        }
    }

    private void OpenSessionWindow()
    {
        var existing = _sessionWindows.FirstOrDefault(window => !window.IsDisposed && !window._sessionClosing &&
            !window._previewOnly &&
            string.Equals(window._appSettings.Connection.Hostname, _appSettings.Connection.Hostname, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(window._appSettings.Connection.Username, _appSettings.Connection.Username, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(window._appSettings.Connection.Domain, _appSettings.Connection.Domain, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.ApplyConnectionSettings(_appSettings.Connection);
            ActivateSession(existing);
            ArrangeSessions();
            return;
        }

        var sessionWindow = new FormRdpClient(Array.Empty<string>(), isSessionWindow: true, managementWindow: this)
        {
            _appSettings = new ApplicationSettings
            {
                Connection = _appSettings.Connection.Clone(),
                NoCloseOnDisconnect = true,
            },
        };
        var previewKey = PreviewKey(_appSettings.Connection);
        if (_previewSessions.TryGetValue(previewKey, out var preview) && !preview.IsDisposed &&
            preview.PrepareForInteractive(_appSettings.Connection) &&
            sessionWindow.AdoptPreviewSession(preview))
        {
            _previewSessions.Remove(previewKey);
            preview.Dispose();
        }
        else
        {
            _previewSessions.Remove(previewKey);
            preview?.Dispose();
        }
        sessionWindow._formSizeEnforcementService.IsActive = sessionWindow._appSettings.Connection.AutoResize == true ||
                                                              sessionWindow._appSettings.Connection.AutoMaximizeWindow == true;
        sessionWindow._globalControlBarAutoHide = _globalControlBarAutoHide;
        sessionWindow._glassChromeStrip?.SetAutoHide(_globalControlBarAutoHide);
        sessionWindow._launchedFromLibrary = true;
        _sessionWindow = sessionWindow;
        _sessionWindows.Add(sessionWindow);
        // Keep the session window independent from the management window so
        // hiding the console into the tray never hides the active connection.
        sessionWindow.Show();
        ArrangeSessions();
        // Connecting from the library is a single-window workflow. The
        // management console stays available from the tray and the session
        // chrome, but must not flash back over the remote desktop after the
        // ActiveX control reports Connected.
        Hide();
    }

    private static string PreviewKey(ConnectionSettings settings)
        => $"{settings.Hostname}\n{settings.Domain}\n{settings.Username}";

    private FormRdpClient? FindPreviewSession(ConnectionSettings settings)
        => _sessionWindows.FirstOrDefault(s => !s.IsDisposed && !s._sessionClosing &&
            !s._previewOnly &&
            string.Equals(PreviewKey(s._appSettings.Connection), PreviewKey(settings), StringComparison.OrdinalIgnoreCase));

    private void StartPreview(ConnectionSettings settings)
    {
        if (!GlobalSettingsService.Load().Previews.Enabled) return;
        if (FindPreviewSession(settings) != null) return;

        var key = PreviewKey(settings);
        if (!_previewSessions.TryGetValue(key, out var preview) || preview.IsDisposed)
        {
            preview?.Dispose();
            _previewSessions[key] = new PreviewSessionHost(settings);
        }
    }

    private Form? GetPreviewSource(ConnectionSettings settings)
    {
        if (FindPreviewSession(settings) is { } session)
            return session.IsHandleCreated ? session : null;
        return null;
    }

    private PreviewSessionHost? GetPreviewHost(ConnectionSettings settings)
    {
        var key = PreviewKey(settings);
        if (_previewSessions.TryGetValue(key, out var preview) && !preview.IsDisposed)
            return preview;
        _previewSessions.Remove(key);
        return null;
    }

    private ConnectionQualitySnapshot? GetConnectionQuality(ConnectionSettings settings)
    {
        var session = FindPreviewSession(settings);
        if (session is { IsDisposed: false })
            return session._rdpService.ConnectionQuality;

        return GetPreviewHost(settings)?.ConnectionQuality;
    }

    private void StopPreview(ConnectionSettings settings)
    {
        var key = PreviewKey(settings);
        if (_previewSessions.Remove(key, out var preview))
            preview.Dispose();
    }

    private void WireRdpEvents()
    {
        _rdpService.Connected += RdpService_Connected;
        _rdpService.LoginCompleted += RdpService_LoginCompleted;
        _rdpService.Disconnected += RdpService_Disconnected;
        _rdpService.LeaveFullScreenRequested += RdpService_LeaveFullScreenRequested;
        _rdpService.FullScreenChanged += RdpService_FullScreenChanged;
        _rdpService.Minimized += RdpService_Minimized;
    }

    private void UnwireRdpEvents()
    {
        _rdpService.Connected -= RdpService_Connected;
        _rdpService.LoginCompleted -= RdpService_LoginCompleted;
        _rdpService.Disconnected -= RdpService_Disconnected;
        _rdpService.LeaveFullScreenRequested -= RdpService_LeaveFullScreenRequested;
        _rdpService.FullScreenChanged -= RdpService_FullScreenChanged;
        _rdpService.Minimized -= RdpService_Minimized;
    }

    private bool AdoptPreviewSession(PreviewSessionHost preview)
    {
        if (!preview.TryTakeService(out var adoptedService, out var oldSurface))
            return false;

        UnwireRdpEvents();
        var unusedService = _rdpService;
        _rdpService = adoptedService;
        _rdpService.AdoptConnectionSettings(_appSettings.Connection);
        _rdpService.MoveContainer(panelRdp);
        _rdpService.SetPreviewMode(false);
        WireRdpEvents();
        var wantsFullScreen = _appSettings.Connection.ScreenMode == ScreenMode.FullScreen ||
                              _appSettings.Connection.UseAllMonitors == true;
        _restoreFullScreenOnConnect = wantsFullScreen;
        if (_rdpService.IsLoginComplete)
            RdpService_LoginCompleted(_rdpService, EventArgs.Empty);
        unusedService.Dispose();
        oldSurface?.Dispose();
        _adoptedPreviewSession = true;
        panelStartup.Visible = false;
        panelConnect.Visible = false;
        panelRdp.Visible = true;
        panelRdp.BringToFront();
        ApplyKeyboardModeForCurrentWindow();
        return true;
    }

    private void PreparePreviewWindow()
    {
        if (!_previewOnly) return;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None;
        ControlBox = false;
        MinimizeBox = false;
        MaximizeBox = false;
        var virtualScreen = SystemInformation.VirtualScreen;
        Bounds = new Rectangle(virtualScreen.Right + 2048, virtualScreen.Top, 1280, 720);
        _rdpService.SetPreviewMode(true);
        panelRdp.Visible = true;
        panelRdp.BringToFront();
    }

    private void PromotePreviewToInteractive()
    {
        if (!_previewOnly) return;
        _previewOnly = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        var work = Screen.FromControl(_managementWindow ?? this).WorkingArea;
        if (Bounds.Left >= SystemInformation.VirtualScreen.Right ||
            Bounds.Right <= SystemInformation.VirtualScreen.Left)
            Bounds = new Rectangle(work.Left, work.Top, Math.Min(1280, work.Width), Math.Min(720, work.Height));
        SetRemoteConnectionChrome(true);
        UpdateWindowTitle();
    }

    private void ActivateSession(FormRdpClient session)
    {
        if (session.IsDisposed || session._sessionClosing) return;
        _sessionWindow = session;
        if (!session.Visible) session.Show();
        if (session.WindowState == FormWindowState.Minimized)
            session.WindowState = FormWindowState.Normal;
        session.PromotePreviewToInteractive();
        session.Activate();
        session.ApplyKeyboardModeForCurrentWindow();
        session._rdpService.FocusRemoteClient();
        session.RefreshGlobalShortcutHookOrder();
    }

    private void RefreshDeviceButtons()
    {
        var sessions = _sessionWindows.Where(s => !s.IsDisposed && !s._sessionClosing && !s._previewOnly).ToArray();
        foreach (var owner in sessions)
        {
            owner._glassChromeStrip?.SetDevices(sessions.Select(session =>
            {
                var name = string.IsNullOrWhiteSpace(session._appSettings.Connection.DisplayName)
                    ? session._appSettings.Connection.Hostname ?? "?" : session._appSettings.Connection.DisplayName;
                return new GlassWindowControlStrip.DeviceButton(name, session == owner,
                    () => ActivateSession(session))
                {
                    AccentColor = DeviceAccentColors.Resolve(session._appSettings.Connection),
                };
            }).ToArray());
        }
    }

    private void ShowGlobalSettings()
    {
        if (_globalSettingsWindow is { IsDisposed: false })
        {
            if (_globalSettingsWindow.WindowState == FormWindowState.Minimized)
                _globalSettingsWindow.WindowState = FormWindowState.Normal;
            _globalSettingsWindow.Activate();
            return;
        }
        var window = new GlobalSettingsForm(GlobalSettingsService.Load());
        _globalSettingsWindow = window;
        window.SettingsSaved += (_, _) =>
        {
            _autoTileSessions = window.SavedSettings.Windows.AutoTileSessions;
            _globalControlBarPosition = window.SavedSettings.Windows.ControlBarDock;
            _globalControlBarAutoHide = window.SavedSettings.Windows.ControlBarAutoHide;
            _globalShortcutPreferences = window.SavedSettings.Shortcuts.Clone();
            RefreshShortcutAvailability();
            // RDP may install/reinstall its native keyboard hook while the
            // settings window is open. Put PureRDP's own shortcut hook back
            // at the front after the local-only policy changes.
            _globalShortcutFilter?.RefreshHook();
            _availabilityScanner?.SetInterval(window.SavedSettings.Monitoring.AvailabilityScanIntervalSeconds);
            if (!window.SavedSettings.Previews.Enabled)
            {
                foreach (var preview in _previewSessions.Values.ToArray())
                    preview.Dispose();
                _previewSessions.Clear();
            }
            _connectionLibraryPanel?.ApplyPreviewEnabled(window.SavedSettings.Previews.Enabled);
            UpdateAvailabilityScanDevices(scanNow: true);
            foreach (var session in _sessionWindows)
            {
                session._globalControlBarAutoHide = _globalControlBarAutoHide;
                session._glassChromeStrip?.SetAutoHide(_globalControlBarAutoHide);
                session.ApplyKeyboardModeForCurrentWindow();
                session.PositionWindowChrome();
            }
            ArrangeSessions();
        };
        window.FormClosed += (_, _) =>
        {
            _globalSettingsWindow = null;
        };
        // Keep the non-modal settings window owned by the console while
        // explicitly centering it on the current screen.
        window.Show(this);
    }

    private void ArrangeSessions()
    {
        if (_isSessionWindow || _arrangingSessions || _allowApplicationExit || IsDisposed) return;
        _arrangingSessions = true;
        try
        {
            var sessions = _sessionWindows.Where(s => !s.IsDisposed && !s._sessionClosing && !s._previewOnly).ToArray();
            RefreshDeviceButtons();
            if (!_autoTileSessions || sessions.Length < 2)
            {
                foreach (var session in sessions)
                {
                    session._rdpService.SetTileMode(false);
                    if (session._independentBounds is not Rectangle bounds) continue;
                    session._independentBounds = null;
                    session.WindowState = FormWindowState.Normal;
                    session.Bounds = bounds;
                    session.WindowState = session._independentState;
                    if (session._independentFullScreen) session._rdpService.FullScreen = true;
                }
                return;
            }
            if (sessions.Length == 0) return;
            var work = Screen.FromControl(this).WorkingArea;
            // Fill across before adding rows: 2 uses two columns, 4 uses 2x2,
            // and 9 uses 3x3. Partial rows retain the same tile dimensions.
            var columns = ChooseGridColumns(sessions.Length);
            var rows = (sessions.Length + columns - 1) / columns;
            var aspectRatio = GetGridAspectRatio(sessions);
            var tileWidth = Math.Max(1, work.Width / columns);
            var maxWidthForHeight = Math.Max(1,
                (int)Math.Floor(work.Height * aspectRatio / rows));
            tileWidth = Math.Min(tileWidth, maxWidthForHeight);
            var tileHeight = Math.Max(1, (int)Math.Round(tileWidth / aspectRatio));

            // Keep the mosaic within the working area even after rounding.
            if ((long)tileHeight * rows > work.Height)
            {
                tileHeight = Math.Max(1, work.Height / rows);
                tileWidth = Math.Max(1, (int)Math.Floor(tileHeight * aspectRatio));
            }
            var totalWidth = tileWidth * columns;
            var originX = work.Left + Math.Max(0, (work.Width - totalWidth) / 2);
            // Keep the first row attached to the top of the working area. The
            // unused space, when a single row does not need the full height,
            // belongs below the mosaic rather than above it.
            var originY = work.Top;
            for (var index = 0; index < sessions.Length; index++)
            {
                var session = sessions[index];
                if (session._independentBounds == null)
                {
                    session._independentFullScreen = session._rdpService.FullScreen;
                    session._independentState = session._rdpService.FullScreen ? session._nonFullScreenState :
                        session.WindowState == FormWindowState.Minimized ? FormWindowState.Normal : session.WindowState;
                    session._independentBounds = session._rdpService.FullScreen ? session._nonFullScreenBounds :
                        session.WindowState == FormWindowState.Normal ? session.Bounds : session.RestoreBounds;
                }
                // A session can have remembered fullscreen state waiting to
                // be restored on its first connection.  Once it belongs to
                // the active grid, that pending restore must be cancelled as
                // well as the current fullscreen state.
                session._restoreFullScreenOnConnect = false;
                if (session._rdpService.FullScreen) session._rdpService.FullScreen = false;
                session.WindowState = FormWindowState.Normal;
                var row = index / columns;
                var col = index % columns;
                session._rdpService.SetTileMode(true);
                // Keep the host windows themselves identical in size.  The
                // RDP surface is laid out inside the slot and preserves each
                // device's aspect ratio, so different remote resolutions can
                // never make one pane taller, overlap a neighbour, or get
                // cropped by the grid.
                var slotX = originX + col * tileWidth;
                var slotY = originY + row * tileHeight;
                session.Bounds = new Rectangle(slotX, slotY, tileWidth, tileHeight);
                session.QueueRemoteResize();
            }
        }
        finally { _arrangingSessions = false; }
    }

    private void QueueSessionCloseRecovery()
    {
        if (_isSessionWindow || _allowApplicationExit || _sessionClosing || IsDisposed || !IsHandleCreated ||
            _sessionCloseRecoveryQueued)
            return;

        _sessionCloseRecoveryQueued = true;
        try
        {
            BeginInvoke(new Action(() =>
            {
                _sessionCloseRecoveryQueued = false;
                if (IsDisposed || _sessionClosing || _allowApplicationExit)
                    return;
                if (_arrangingSessions)
                {
                    QueueSessionCloseRecovery();
                    return;
                }

                // Reflow after the closed HWND has completely left the native
                // window hierarchy. Running this from FormClosed synchronously
                // can leave the remaining ActiveX surface without focus or a
                // valid z-order until the user minimizes and restores it.
                ArrangeSessions();

                var sessions = GetInteractiveSessions();
                var active = _sessionWindow is { IsDisposed: false } preferred &&
                              !preferred._sessionClosing && preferred.Visible &&
                              preferred.WindowState != FormWindowState.Minimized
                    ? preferred
                    : sessions.FirstOrDefault(session => session.Visible &&
                        session.WindowState != FormWindowState.Minimized);
                if (active == null)
                {
                    _sessionWindow = sessions.FirstOrDefault();
                    return;
                }

                // Restore z-order and the native child focus without changing
                // the selected window's Normal/Maximized/true-fullscreen state.
                // In particular, closing a sibling must never flatten a full
                // screen connection as a side effect of recovery.
                _sessionWindow = active;
                active.BringToFront();
                active.Activate();
                if (active.IsHandleCreated)
                    WindowChromeInterop.SetForegroundWindow(active.Handle);
                active.ApplyKeyboardModeForCurrentWindow();
                if (active._rdpService.IsConnected)
                {
                    active._rdpService.FocusRemoteClient();
                    active.RefreshGlobalShortcutHookOrder();
                }
            }));
        }
        catch (InvalidOperationException)
        {
            _sessionCloseRecoveryQueued = false;
        }
    }

    private static int ChooseGridColumns(int count)
    {
        if (count <= 1) return 1;
        return (int)Math.Ceiling(Math.Sqrt(count));
    }

    private static double GetGridAspectRatio(IEnumerable<FormRdpClient> sessions)
    {
        var ratios = sessions
            .Select(session =>
            {
                var width = session._appSettings.Connection.Width ?? 1600;
                var height = session._appSettings.Connection.Height ?? 900;
                return width > 0 && height > 0 ? (double)width / height : 16.0 / 9.0;
            })
            .Where(ratio => ratio is >= 0.75 and <= 3.5)
            .ToArray();
        return ratios.Length == 0 ? 16.0 / 9.0 : ratios.Average();
    }

    private void InitializeTrayIcon()
    {
        _trayMenu = new ContextMenuStrip();
        _trayMenu.Items.Add("打开主页面", null, (_, _) => ShowManagementWindow());
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("确定退出", null, (_, _) => ExitApplication());

        _trayIcon = new NotifyIcon
        {
            Icon = Properties.Resources.AppIcon,
            Text = "PureRDP",
            ContextMenuStrip = _trayMenu,
            Visible = true,
        };
        _trayIcon.MouseClick += TrayIcon_MouseClick;
    }

    private void TrayIcon_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            ShowManagementWindow();
    }

    private void ExitApplication()
    {
        if (_allowApplicationExit)
            return;

        _allowApplicationExit = true;
        foreach (var session in _sessionWindows.ToArray())
        {
            if (session.IsDisposed) continue;
            session._sessionClosing = true;
            session.Close();
        }

        if (_trayIcon != null)
            _trayIcon.Visible = false;
        Close();
    }

    private void AutoStartup()
    {
        var editMode = _appSettings.EditRdpFile == true;
        if (editMode)
        {
            if (ShowSettings() && _settingsConnectRequested)
                Connect();
            return;
        }

        // With no command-line target, open the connection library instead of
        // forcing the user through the settings dialog every time.
        if (!_appSettings.Connection.IsValid())
        {
            ShowConnectionLibrary();
            return;
        }

        var autoConnectConfigured = _appSettings.NoAutoConnect != true;
        var autoConnectSuppressed = ModifierKeys.HasFlag(Keys.Shift);
        if (autoConnectConfigured && !autoConnectSuppressed)
            Connect();
        else
            ShowConnectionLibrary();
    }

    private void InitializeSurfaceVisibility()
    {
        // The management surface is a normal Windows window. The RDP canvas is
        // hidden until a session is actually connected so it cannot cover the
        // library controls with an empty native client surface.
        panelRdp.Visible = false;
        panelConnect.Visible = false;
        panelStartup.Visible = true;
        panelStartup.BringToFront();
    }

    private void InitializeWindowChrome()
    {
        // Keep the management page and settings dialog on the normal Windows
        // title bar. The compact glass control strip is only enabled for the
        // connected remote-desktop surface.
        FormBorderStyle = FormBorderStyle.Sizable;
        ControlBox = true;
        MaximizeBox = true;
        MinimizeBox = true;

        _glassChromeStrip = new GlassWindowControlStrip
        {
            Visible = false,
        };
        _glassChromeStrip.ManagementRequested += GlassChromeStrip_ManagementRequested;
        _glassChromeStrip.MinimizeRequested += GlassChromeStrip_MinimizeRequested;
        _glassChromeStrip.MaximizeRequested += GlassChromeStrip_MaximizeRequested;
        _glassChromeStrip.CloseRequested += GlassChromeStrip_CloseRequested;
        _glassChromeStrip.ToggleFullScreenRequested += (_, _) => ToggleFullScreen();
        _glassChromeStrip.ExpandedChanged += (_, _) => PositionWindowChrome();
        _glassChromeStrip.Owner = this;
        _cornerGrips = new[] { Interop.HTBOTTOMLEFT, Interop.HTBOTTOMRIGHT }
            .Select(hit => new GlassWindowControlStrip(hit) { Owner = this }).ToArray();
        LocationChanged += (_, _) => PositionWindowChrome();

        SizeChanged += (_, _) =>
        {
            PositionWindowChrome();
            QueueRemoteResize();
            ApplyKeyboardModeForCurrentWindow();
        };
        Resize += (_, _) =>
        {
            if (_sessionClosing || Disposing || IsDisposed)
                return;
            if (_isSessionWindow && WindowState != FormWindowState.Minimized &&
                !(_rdpService?.FullScreen ?? false))
            {
                _lastVisibleWindowState = WindowState;
                _lastVisibleWindowBounds = WindowState == FormWindowState.Normal
                    ? Bounds : RestoreBounds;
                if (_lastVisibleWindowBounds.Width <= 0 || _lastVisibleWindowBounds.Height <= 0)
                    _lastVisibleWindowBounds = Bounds;
                _hasLastVisibleWindowState = true;
            }
            if (_isSessionWindow && _rdpService?.FullScreen == true &&
                !_fullScreenTransitionInProgress &&
                WindowState != FormWindowState.Minimized)
            {
                var fullScreenBounds = GetTotalMonitorBounds(_fullScreenMultiMonitor);
                if (Bounds != fullScreenBounds)
                    Bounds = fullScreenBounds;
            }
            ResumeDisplayAfterExternalRestore();
            PositionWindowChrome();
            _glassChromeStrip?.UpdateMaximizeGlyph(WindowState == FormWindowState.Maximized || _rdpService?.FullScreen == true);
        };
    }

    private void SetRemoteConnectionChrome(bool enabled)
    {
        _remoteChromeActive = enabled;
        if (_previewOnly)
            return;
        if (enabled)
        {
            ApplyRemoteWindowChrome();
            if (WindowState == FormWindowState.Normal && !IsGridLayoutActive)
                TryRestoreManualWindowBounds();
        }
        else
            ApplyNativeWindowChrome();
    }

    private void ApplyRemoteWindowChrome()
    {
        if (_sessionClosing || Disposing || IsDisposed)
            return;
        var wasMaximized = WindowState == FormWindowState.Maximized;
        var restoreBounds = RestoreBounds;
        if (wasMaximized)
            WindowState = FormWindowState.Normal;
        FormBorderStyle = FormBorderStyle.None;
        ControlBox = false;
        MaximizeBox = false;
        MinimizeBox = false;
        if (wasMaximized)
        {
            if (restoreBounds.Width > 0 && restoreBounds.Height > 0)
                Bounds = restoreBounds;
            WindowState = FormWindowState.Maximized;
        }

        if (_glassChromeStrip == null || _glassChromeStrip.IsDisposed)
            return;

        _glassChromeStrip.SetGlassMode(true);
        _glassChromeStrip.SetAutoHide(_globalControlBarAutoHide);
        if (ShouldHideControlBar())
            _glassChromeStrip.Hide();
        else if (!_glassChromeStrip.Visible)
            _glassChromeStrip.Show(this);
        _glassChromeStrip.UpdateMaximizeGlyph(WindowState == FormWindowState.Maximized || _rdpService?.FullScreen == true);
        PositionWindowChrome();
    }

    private void ApplyNativeWindowChrome()
    {
        if (_sessionClosing || Disposing || IsDisposed)
            return;
        foreach (var grip in _cornerGrips) if (!grip.IsDisposed) grip.Hide();
        if (_glassChromeStrip is { IsDisposed: false })
        {
            _glassChromeStrip.SetGlassMode(false);
            _glassChromeStrip.Visible = false;
        }

        FormBorderStyle = FormBorderStyle.Sizable;
        ControlBox = true;
        MaximizeBox = true;
        MinimizeBox = true;
    }

    private void PositionWindowChrome()
    {
        if (_glassChromeStrip == null || _glassChromeStrip.IsDisposed || IsDisposed ||
            Disposing || _sessionClosing || !_remoteChromeActive || !Visible ||
            WindowState == FormWindowState.Minimized)
            return;

        var position = _managementWindow?._globalControlBarPosition ?? _globalControlBarPosition;
        _glassChromeStrip.SetPosition(position);
        var stripX = position switch
        {
            ControlBarPosition.Top or ControlBarPosition.Bottom
                => Math.Max(0, (ClientSize.Width - _glassChromeStrip.Width) / 2),
            ControlBarPosition.Left => 0,
            _ => Math.Max(0, ClientSize.Width - _glassChromeStrip.Width),
        };
        var stripY = position switch
        {
            ControlBarPosition.Top => 0,
            ControlBarPosition.Bottom => Math.Max(0, ClientSize.Height - _glassChromeStrip.Height),
            ControlBarPosition.Left or ControlBarPosition.Right
                => Math.Max(0, (ClientSize.Height - _glassChromeStrip.Height) / 2),
            _ => Math.Max(0, (ClientSize.Height - _glassChromeStrip.Height) / 2),
        };
        _glassChromeStrip.Location = PointToScreen(new Point(stripX, stripY));
        if (Form.ActiveForm == this) _glassChromeStrip.BringToFront();
        var corners = new[] { new Point(0, ClientSize.Height - ResizeGripSize),
            new Point(ClientSize.Width - ResizeGripSize, ClientSize.Height - ResizeGripSize) };
        for (var i = 0; i < _cornerGrips.Length; i++)
        {
            var grip = _cornerGrips[i];
            if (grip.IsDisposed) continue;
            if (!IsRemoteWindowResizable) { grip.Hide(); continue; }
            grip.Location = PointToScreen(corners[i]);
            if (Visible && !grip.Visible) grip.Show(this);
            if (Form.ActiveForm == this) grip.BringToFront();
        }
    }

    internal bool IsRemoteWindowResizable
        => _remoteChromeActive && !_rdpService.FullScreen && WindowState != FormWindowState.Minimized;

    private bool _windowMovePending, _windowMoveActive;
    private Point _windowMoveOrigin;
    private Rectangle _windowMoveStartBounds;
    private System.Windows.Forms.Timer? _windowMoveTimer;
    internal bool IsRemoteWindowMovable => _isSessionWindow && !_previewOnly &&
        _remoteChromeActive && !_rdpService.FullScreen && !_fullScreenTransitionInProgress &&
        !_sessionClosing && !IsDisposed && !Disposing && IsHandleCreated && WindowState != FormWindowState.Minimized;

    internal bool BeginRemoteWindowMove(Point origin)
    {
        if (!IsRemoteWindowMovable) return false;
        EndRemoteWindowMove();
        EndRemoteWindowResize();
        Activate();
        _windowMoveOrigin = origin;
        _windowMoveStartBounds = Bounds;
        _windowMovePending = true;
        _windowMoveTimer ??= CreateWindowMoveTimer();
        WindowChromeInterop.SetCapture(Handle);
        _windowMoveTimer.Start();
        return true;
    }

    private System.Windows.Forms.Timer CreateWindowMoveTimer()
    {
        var timer = new System.Windows.Forms.Timer { Interval = 16 };
        timer.Tick += (_, _) =>
        {
            if (!IsRemoteWindowMovable || (Control.MouseButtons & MouseButtons.Left) == 0)
                EndRemoteWindowMove();
            else UpdateRemoteWindowMove(Cursor.Position);
        };
        return timer;
    }

    internal void UpdateRemoteWindowMove(Point cursor)
    {
        if ((!_windowMovePending && !_windowMoveActive) || !IsRemoteWindowMovable) return;
        if (_windowMovePending)
        {
            var threshold = SystemInformation.DragSize;
            if (new Rectangle(_windowMoveOrigin.X - threshold.Width / 2,
                _windowMoveOrigin.Y - threshold.Height / 2, threshold.Width, threshold.Height).Contains(cursor)) return;
            _windowMovePending = false;
            _windowMoveActive = true;
            if (WindowState == FormWindowState.Maximized)
            {
                var restore = RestoreBounds;
                var work = Screen.FromControl(this).WorkingArea;
                if (restore.Width < 320 || restore.Height < 240 || restore.Size == Bounds.Size)
                    restore.Size = new Size(Math.Max(320, work.Width * 3 / 4), Math.Max(240, work.Height * 3 / 4));
                var anchor = Math.Clamp((_windowMoveOrigin.X - _windowMoveStartBounds.Left) /
                    (double)Math.Max(1, _windowMoveStartBounds.Width), 0, 1);
                var topOffset = Math.Clamp(_windowMoveOrigin.Y - _windowMoveStartBounds.Top, 8, 48);
                WindowState = FormWindowState.Normal;
                Bounds = new Rectangle(cursor.X - (int)(restore.Width * anchor), cursor.Y - topOffset,
                    restore.Width, restore.Height);
                _windowMoveOrigin = cursor;
                _windowMoveStartBounds = Bounds;
            }
        }
        // Move only this host, including when it is a member of the grid.
        Location = new Point(_windowMoveStartBounds.Left + cursor.X - _windowMoveOrigin.X,
            _windowMoveStartBounds.Top + cursor.Y - _windowMoveOrigin.Y);
    }

    internal void EndRemoteWindowMove()
    {
        _windowMoveTimer?.Stop();
        var moving = _windowMovePending || _windowMoveActive;
        var moved = _windowMoveActive;
        _windowMovePending = _windowMoveActive = false;
        if (moving && IsHandleCreated && WindowChromeInterop.GetCapture() == Handle)
            WindowChromeInterop.ReleaseCapture();
        if (moved) RememberManualWindowBounds();
    }

    internal bool TryHandleWindowMoveInput(ref Message message)
    {
        // In true fullscreen do not consume ANY chord or mouse event.
        if (!IsRemoteWindowMovable) return false;
        if (message.Msg is not (Interop.WM_LBUTTONDOWN or Interop.WM_LBUTTONUP or Interop.WM_MOUSEMOVE or
            Interop.WM_NCLBUTTONDOWN or Interop.WM_NCLBUTTONUP or 0x0203)) return false;
        var packed = message.LParam.ToInt64();
        var point = new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff));
        if (message.Msg is not (Interop.WM_NCLBUTTONDOWN or Interop.WM_NCLBUTTONUP) &&
            !WindowChromeInterop.ClientToScreen(message.HWnd, ref point)) return false;
        if (_windowMovePending || _windowMoveActive)
        {
            if (message.Msg is Interop.WM_LBUTTONUP or Interop.WM_NCLBUTTONUP) EndRemoteWindowMove();
            else if (message.Msg == Interop.WM_MOUSEMOVE) UpdateRemoteWindowMove(point);
            return true;
        }
        return false;
    }

    internal bool IsToolbarDragPoint(Point screenPoint)
    {
        if (_glassChromeStrip is not { Visible: true } strip) return false;
        return strip.IsInteractivePoint(screenPoint);
    }

    internal bool IsToolbarArea(Point screenPoint)
    {
        if (_glassChromeStrip is not { Visible: true } strip || strip.IsDisposed)
            return false;

        // Padding belongs to the toolbar, not to a native RDP resize edge.
        return strip.ClientRectangle.Contains(strip.PointToClient(screenPoint));
    }

    private bool ShouldHideControlBar()
        => _rdpService.FullScreen
            ? _appSettings.Connection.ControlBarHiddenFullscreen
            : _appSettings.Connection.ControlBarHiddenWindowed;

    private readonly Dictionary<Keys, (FormRdpClient Window, FormWindowState State)> _shortcutReturns = new();
    private readonly Dictionary<FormRdpClient, SessionWindowStateSnapshot> _allSessionMinimizeSnapshots = new();
    private FormRdpClient? _allSessionMinimizePreferred;
    private bool _allSessionsMinimized;
    private bool _allSessionMinimizePending;
    private FormWindowState _lastVisibleWindowState = FormWindowState.Normal;
    private Rectangle _lastVisibleWindowBounds;
    private bool _hasLastVisibleWindowState;

    private void ResumeDisplayAfterExternalRestore()
    {
        var management = _managementWindow ?? this;
        // Group minimize owns this pause. Restoring through the taskbar/shell
        // never enters our group restore branch, so release only that host's
        // pause once it is restored. Never unpause minimized siblings or an
        // in-progress bulk/fullscreen transaction.
        if (!_isSessionWindow || _previewOnly || _sessionClosing || Disposing || IsDisposed ||
            WindowState == FormWindowState.Minimized || _restoringControlBarState ||
            _fullScreenTransitionInProgress || management._bulkWindowStateTransition ||
            _rdpService?.DisplayUpdatesSuspended != true ||
            !management._allSessionMinimizeSnapshots.ContainsKey(this))
            return;

        _rdpService.ResumeDisplayAfterWindowRestore();
        ConnectionLogService.WriteDiagnostic("display-resume source=external-restore");
        QueueRemoteResize();
    }

    private FormRdpClient? GetForegroundSession()
    {
        var foreground = WindowChromeInterop.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return null;
        var root = WindowChromeInterop.GetAncestor(foreground, 3); // GA_ROOTOWNER, includes floating grips
        return _sessionWindows.FirstOrDefault(window =>
            !window.IsDisposed && !window._sessionClosing && !window._previewOnly &&
            window.IsHandleCreated && window.Visible && window.WindowState != FormWindowState.Minimized &&
            (window.Handle == foreground || window.Handle == root ||
             WindowChromeInterop.IsChild(window.Handle, foreground) ||
             (window._glassChromeStrip is { IsHandleCreated: true } strip &&
              (strip.Handle == foreground || WindowChromeInterop.IsChild(strip.Handle, foreground)))));
    }

    private bool ToggleAllSessionMinimize(bool queueOnly)
    {
        if (_allSessionMinimizePending)
        {
            ConnectionLogService.WriteDiagnostic("minimize-command coalesced=pending");
            return true;
        }

        var targets = GetInteractiveSessions().ToArray();
        _allSessionMinimizePending = true;
        void Apply()
        {
            try
            {
                var currentTargets = targets
                    .Where(window => !window.IsDisposed && !window._sessionClosing && !window._previewOnly)
                    .ToArray();
                if (currentTargets.Length == 0)
                {
                    _allSessionMinimizeSnapshots.Clear();
                    _allSessionMinimizePreferred = null;
                    _allSessionsMinimized = false;
                    return;
                }

                // Taskbar/Alt+Tab/ActiveX can restore a host without running our
                // group restore path. The old flag then makes the first chord
                // redundantly restore an already visible window. Reconcile at
                // dispatch time, including an external restore of only one host.
                if (_allSessionsMinimized && currentTargets.Any(window =>
                    window.WindowState != FormWindowState.Minimized &&
                    (!window.IsHandleCreated || !WindowChromeInterop.IsIconic(window.Handle))))
                {
                    _allSessionsMinimized = false;
                    _allSessionMinimizeSnapshots.Clear();
                    _allSessionMinimizePreferred = null;
                    ConnectionLogService.WriteDiagnostic("minimize-command resync=externally-restored");
                }
                ConnectionLogService.WriteDiagnostic($"minimize-command action={(_allSessionsMinimized ? "restore" : "minimize")} targets={currentTargets.Length}");

                // A session can have been minimized from the taskbar or by
                // the shell instead of through PureRDP's own control strip.
                // In that case the toggle flag is not set yet. Treat the
                // shortcut as a restore and reconstruct the last visible
                // window/fullscreen state instead of taking a minimized
                // snapshot that would restore back to Minimized.
                if (!_allSessionsMinimized &&
                    currentTargets.All(window => window.WindowState == FormWindowState.Minimized))
                {
                    RestoreManuallyMinimizedSessions(currentTargets);
                    return;
                }

                if (!_allSessionsMinimized)
                {
                    _bulkWindowStateTransition = true;
                    var suspended = new List<FormRdpClient>();
                    try
                    {
                        _allSessionMinimizeSnapshots.Clear();
                        _allSessionMinimizePreferred = GetForegroundSession() ??
                            (_sessionWindow is { } preferred && currentTargets.Contains(preferred)
                                ? preferred : currentTargets[0]);
                        foreach (var session in currentTargets)
                        {
                            session._rdpService.SetDisplayUpdatesSuspended(true);
                            suspended.Add(session);
                            _allSessionMinimizeSnapshots[session] = session.CaptureWindowState();
                        }

                        // Minimize the top-level forms together. Do not touch the
                        // RDP FullScreen flag: a true-fullscreen session must come
                        // back as true fullscreen, not as an ordinary window.
                        foreach (var session in currentTargets)
                            if (!session.IsDisposed && !session._sessionClosing)
                            {
                                session.WindowState = FormWindowState.Minimized;
                                ConnectionLogService.WriteDiagnostic($"minimize-result state={session.WindowState} iconic={WindowChromeInterop.IsIconic(session.Handle)} fullscreen={session._rdpService.FullScreen}");
                            }
                        _allSessionsMinimized = true;
                        EndBulkWindowStateTransition();
                    }
                    catch
                    {
                        foreach (var session in suspended)
                            if (!session.IsDisposed && !session._sessionClosing)
                                session._rdpService.SetDisplayUpdatesSuspended(false, refreshOnResume: false);
                        _allSessionMinimizeSnapshots.Clear();
                        _allSessionMinimizePreferred = null;
                        _allSessionsMinimized = false;
                        _bulkWindowStateTransition = false;
                        throw;
                    }
                }
                else
                {
                    var snapshots = _allSessionMinimizeSnapshots.ToArray();
                    _bulkWindowStateTransition = true;
                    _allSessionsMinimized = false;
                    _allSessionMinimizeSnapshots.Clear();
                    try
                    {
                        foreach (var (session, snapshot) in snapshots)
                        {
                            if (!session.IsDisposed && !session._sessionClosing)
                                RestoreWindowState(session, snapshot);
                        }
                    }
                    finally
                    {
                        // Resume after all host forms reach their final size.
                        // Keep an unchanged viewport instead of renegotiating it.
                        foreach (var (session, _) in snapshots)
                        {
                            if (!session.IsDisposed && !session._sessionClosing)
                                session._rdpService.ResumeDisplayAfterWindowRestore();
                        }
                        EndBulkWindowStateTransition();
                    }

                    var preferred = _allSessionMinimizePreferred;
                    _allSessionMinimizePreferred = null;
                    if (preferred is { IsDisposed: false } && !preferred._sessionClosing &&
                        preferred.WindowState != FormWindowState.Minimized)
                        BringSessionToFront(preferred, forceForeground: true);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or ArgumentException)
            {
                ConnectionLogService.WriteDiagnostic($"minimize-command failed={exception.GetType().Name}");
                _bulkWindowStateTransition = false;
            }
            finally { _allSessionMinimizePending = false; }
        }
        if (queueOnly)
        {
            try { BeginInvoke(new Action(Apply)); }
            catch (InvalidOperationException) { _allSessionMinimizePending = false; return false; }
        }
        else Apply();
        return true;
    }

    private void RestoreManuallyMinimizedSessions(FormRdpClient[] sessions)
    {
        var snapshots = new Dictionary<FormRdpClient, SessionWindowStateSnapshot>();
        var suspended = new List<FormRdpClient>();
        var preferred = _sessionWindow is { } current && sessions.Contains(current)
            ? current : sessions[0];
        _bulkWindowStateTransition = true;
        try
        {
            foreach (var session in sessions)
            {
                if (session.IsDisposed || session._sessionClosing) continue;
                session._rdpService.SetDisplayUpdatesSuspended(true);
                suspended.Add(session);
                snapshots[session] = session.CaptureWindowState();
            }

            foreach (var (session, snapshot) in snapshots)
            {
                if (!session.IsDisposed && !session._sessionClosing)
                    RestoreWindowState(session, snapshot);
            }
        }
        finally
        {
            foreach (var session in suspended)
            {
                if (!session.IsDisposed && !session._sessionClosing)
                    session._rdpService.ResumeDisplayAfterWindowRestore();
            }
            EndBulkWindowStateTransition();
        }

        if (!preferred.IsDisposed && !preferred._sessionClosing &&
            preferred.WindowState != FormWindowState.Minimized)
            BringSessionToFront(preferred, forceForeground: true);
    }

    private void EndBulkWindowStateTransition()
    {
        if (IsDisposed || !IsHandleCreated)
        {
            _bulkWindowStateTransition = false;
            return;
        }

        try
        {
            // Activation/resize notifications raised by restoring a minimized
            // form are already queued. Keep resize suppression through one
            // extra UI turn so those stale callbacks cannot redraw the RDP
            // surface after the guard is cleared.
            BeginInvoke(new Action(() =>
            {
                _bulkWindowStateTransition = false;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        foreach (var session in _sessionWindows)
                        {
                            session._suppressRemoteResizeAfterRestore = false;
                            session.SetRestoreRedraw(true);
                            // The shell can restore a host before these queued
                            // notifications drain. Its Resize handler deferred
                            // resuming while the bulk guard was still active.
                            session.ResumeDisplayAfterExternalRestore();
                        }
                    }));
                }
                catch (InvalidOperationException) { }
            }));
        }
        catch (InvalidOperationException)
        {
            _bulkWindowStateTransition = false;
        }
    }

    private FormRdpClient[] GetInteractiveSessions()
        => _sessionWindows
            .Where(window => !window.IsDisposed && !window._sessionClosing && !window._previewOnly)
            .ToArray();

    private SessionWindowStateSnapshot CaptureWindowState()
    {
        var fullScreen = _rdpService.FullScreen;
        var windowState = WindowState;
        var bounds = fullScreen
            ? _nonFullScreenBounds
            : windowState == FormWindowState.Normal ? Bounds : RestoreBounds;

        if (windowState == FormWindowState.Minimized)
        {
            windowState = fullScreen
                ? _nonFullScreenState
                : _hasLastVisibleWindowState ? _lastVisibleWindowState : FormWindowState.Normal;
            if (!fullScreen && _lastVisibleWindowBounds.Width > 0 &&
                _lastVisibleWindowBounds.Height > 0)
                bounds = _lastVisibleWindowBounds;
        }

        var normalState = fullScreen
            ? _nonFullScreenState
            : windowState == FormWindowState.Minimized ? FormWindowState.Normal : windowState;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            bounds = Bounds;
        var normalBounds = fullScreen && (_nonFullScreenBounds.Width <= 0 ||
                                         _nonFullScreenBounds.Height <= 0)
            ? bounds : _nonFullScreenBounds;
        return new SessionWindowStateSnapshot(windowState, bounds, fullScreen,
            _fullScreenMultiMonitor, normalState, normalBounds);
    }

    private void BringSessionToFront(FormRdpClient session, bool forceForeground = false)
    {
        if (session.IsDisposed || session._sessionClosing)
            return;

        _sessionWindow = session;
        if (!session.Visible)
            session.Show();
        if (session.WindowState == FormWindowState.Minimized)
            session.WindowState = FormWindowState.Normal;

        session.PromotePreviewToInteractive();
        session.BringToFront();
        if (session.IsHandleCreated)
            WindowChromeInterop.BringWindowToTop(session.Handle);
        session.Activate();
        if (session.IsHandleCreated)
        {
            if (forceForeground)
                WindowChromeInterop.SetForegroundWindowReliable(session.Handle);
            else
                WindowChromeInterop.SetForegroundWindow(session.Handle);
        }
        session.ApplyKeyboardModeForCurrentWindow();
        session._rdpService.FocusRemoteClient();
        session.RefreshGlobalShortcutHookOrder();
    }

    private static void RestoreWindowState(FormRdpClient session, SessionWindowStateSnapshot snapshot)
    {
        session._restoringControlBarState = true;
        session._suppressNextActivationVisualRefresh = true;
        session._suppressRemoteResizeAfterRestore = true;
        session.SetRestoreRedraw(false);

        try
        {
            // Seed the return-to-window state before restoring true fullscreen.
            // This keeps a maximized or ordinary window from being flattened
            // when the native RDP object sends its fullscreen notification.
            if (snapshot.FullScreen)
            {
                session._fullScreenMultiMonitor = snapshot.FullScreenMultiMonitor;
                session._nonFullScreenBounds = snapshot.NonFullScreenBounds;
                session._nonFullScreenState = snapshot.NonFullScreenState;
                session.WindowState = FormWindowState.Normal;
                if (!session._rdpService.FullScreen)
                {
                    // This is only a recovery path for an out-of-order native
                    // fullscreen notification. The normal minimize/restore path
                    // keeps FullScreen true and therefore avoids a redraw cycle.
                    session._rdpService.FullScreen = true;
                }
                else
                {
                    var fullScreenBounds = session.GetTotalMonitorBounds(snapshot.FullScreenMultiMonitor);
                    if (session.Bounds != fullScreenBounds)
                        session.Bounds = fullScreenBounds;
                }
                if (snapshot.WindowState == FormWindowState.Minimized)
                    session.WindowState = FormWindowState.Minimized;
                return;
            }

            if (session._rdpService.FullScreen)
                session._rdpService.FullScreen = false;

            // A maximized "window fullscreen" session must be restored by
            // going straight back to Maximized. Setting Normal and then
            // replaying RestoreBounds first briefly exposes the ordinary
            // window size and makes the RDP surface redraw once on the way up.
            if (snapshot.WindowState == FormWindowState.Maximized)
            {
                session.WindowState = FormWindowState.Maximized;
                return;
            }

            session.WindowState = FormWindowState.Normal;
            if (snapshot.Bounds.Width > 0 && snapshot.Bounds.Height > 0)
                session.Bounds = snapshot.Bounds;
            session.WindowState = snapshot.WindowState;
        }
        finally
        {
            session._restoringControlBarState = false;
        }
    }

    private void SetRestoreRedraw(bool enabled)
    {
        if (!_isSessionWindow || IsDisposed)
            return;

        if (_restoreRedrawSuspended == !enabled)
            return;
        _restoreRedrawSuspended = !enabled;
        // Lock only the remote canvas, never the top-level form. WM_SETREDRAW
        // changes WS_VISIBLE, so sending it to the host interferes with native
        // minimize/restore and an unpaired resume can resurrect a hidden form.
        _rdpService.SetNativeRedrawEnabled(enabled);
    }
    private readonly Dictionary<Keys, (long CheckedAt, bool Available)> _shortcutAvailability = new();

    internal bool IsConfiguredShortcutKey(Keys key)
    {
        foreach (var binding in _shortcutAvailability.Keys)
            if ((binding & Keys.KeyCode) == key) return true;
        return false;
    }


    internal void RefreshShortcutAvailability()
    {
        foreach (var text in new[] { _globalShortcutPreferences.MinimizeConnectionWindow,
            _globalShortcutPreferences.MaximizeConnectionWindow, _globalShortcutPreferences.ShowControlConsole,
            _globalShortcutPreferences.ToggleTrueFullscreen, _globalShortcutPreferences.CycleConnectionWindow,
            _globalShortcutPreferences.CloseConnectionWindow })
        {
            var key = ShortcutBinding.Parse(text);
            if (key != Keys.None)
                _shortcutAvailability[key] = (Environment.TickCount64, ShortcutBinding.IsAvailable(key));
        }
    }

    internal bool TryHandleGlobalShortcut(Keys keyData, bool queueOnly = false)
    {
        if (_sessionClosing || IsDisposed)
            return false;
        // Capturing a new binding must never invoke a pending return action.
        if (Form.ActiveForm is GlobalSettingsForm settingsForm && settingsForm.IsHandleCreated &&
            (WindowChromeInterop.GetForegroundWindow() == settingsForm.Handle ||
             WindowChromeInterop.IsChild(settingsForm.Handle, WindowChromeInterop.GetForegroundWindow())))
            return false;

        var bindings = new[] { _globalShortcutPreferences.MinimizeConnectionWindow,
            _globalShortcutPreferences.MaximizeConnectionWindow, _globalShortcutPreferences.ShowControlConsole,
            _globalShortcutPreferences.ToggleTrueFullscreen, _globalShortcutPreferences.CycleConnectionWindow,
            _globalShortcutPreferences.CloseConnectionWindow };
        if (bindings.Count(value => ShortcutBinding.Matches(value, keyData)) != 1)
            return false;
        if (!_shortcutAvailability.TryGetValue(keyData, out var availability) || !availability.Available)
            return false;

        // In sync mode a foreground RDP session configured for remote Windows
        // keys owns the chord. Always-local mode deliberately bypasses that
        // per-connection choice so PureRDP's own shortcuts remain available.
        if (_globalShortcutPreferences.EffectMode == GlobalShortcutEffectMode.SyncConnectionWindow &&
            IsForegroundConnectionUsingRemoteKeyboardMode())
            return false;

        var isMinimize = ShortcutBinding.Matches(_globalShortcutPreferences.MinimizeConnectionWindow, keyData);
        var isConsole = ShortcutBinding.Matches(_globalShortcutPreferences.ShowControlConsole, keyData);
        // The minimize shortcut only controls interactive connection windows.
        // With no such window it must be a no-op; in particular, it must not
        // fall through to the management console behavior.
        if (isMinimize && GetInteractiveSessions().Length == 0)
            return false;
        if (isMinimize) return ToggleAllSessionMinimize(queueOnly);

        var foreground = WindowChromeInterop.GetForegroundWindow();
        var managementWindowIsForeground = foreground == Handle ||
            (IsHandleCreated && WindowChromeInterop.IsChild(Handle, foreground));
        // A remembered console return belongs to the console toggle only when
        // the console is currently in front. If a stale return exists while a
        // connection is foreground, consuming it would activate that old
        // connection on the first press instead of opening the console.
        var canRestoreShortcutReturn = isConsole && managementWindowIsForeground;
        if (canRestoreShortcutReturn && _shortcutReturns.Remove(keyData, out var saved))
        {
            if (!saved.Window.IsDisposed && !saved.Window._sessionClosing && saved.Window._rdpService.IsConnected)
            {
                void Restore()
                {
                    if (saved.Window.IsDisposed || saved.Window._sessionClosing) return;
                    if (saved.Window.WindowState == FormWindowState.Minimized)
                        saved.Window.WindowState = saved.State;
                    ActivateSession(saved.Window);
                }
                if (queueOnly) BeginInvoke(new Action(Restore)); else Restore();
                return true;
            }
        }

        var session = _sessionWindows.FirstOrDefault(window =>
            !window.IsDisposed && !window._sessionClosing && !window._previewOnly &&
            window._rdpService.IsConnected &&
            (window == Form.ActiveForm || window.ContainsFocus ||
             (window.IsHandleCreated &&
              (window.Handle == foreground || WindowChromeInterop.IsChild(window.Handle, foreground))) ||
             (window._glassChromeStrip is { IsHandleCreated: true } strip && strip.Handle == foreground)));
        // The console is an application action, independent of RDP sessions.
        // A remote-origin invocation still remembers that session for return.
        if (isConsole)
        {
            void ToggleConsole()
            {
                if (IsDisposed || _sessionClosing) return;
                if (session is { IsDisposed: false } && !session._sessionClosing)
                {
                    _shortcutReturns[keyData] = (session, session.WindowState);
                    ShowManagementWindow();
                }
                else if (Visible && WindowState != FormWindowState.Minimized &&
                         (WindowChromeInterop.GetForegroundWindow() == Handle || ContainsFocus))
                    Hide();
                else
                    ShowManagementWindow();
            }
            if (queueOnly) BeginInvoke(new Action(ToggleConsole)); else ToggleConsole();
            return true;
        }
        if (session == null)
            return false;

        void Dispatch(Action action)
        {
            if (queueOnly)
                BeginInvoke(new Action(() => { if (!session.IsDisposed && !session._sessionClosing) action(); }));
            else
                action();
        }

        if (ShortcutBinding.Matches(_globalShortcutPreferences.MaximizeConnectionWindow, keyData))
        {
            Dispatch(() => session.GlassChromeStrip_MaximizeRequested(null, EventArgs.Empty));
            return true;
        }
        if (ShortcutBinding.Matches(_globalShortcutPreferences.ToggleTrueFullscreen, keyData))
        {
            Dispatch(() => session.ToggleFullScreen());
            return true;
        }
        if (ShortcutBinding.Matches(_globalShortcutPreferences.CycleConnectionWindow, keyData))
        {
            Dispatch(() => CycleSessionWindow(session));
            return true;
        }
        if (ShortcutBinding.Matches(_globalShortcutPreferences.CloseConnectionWindow, keyData))
        {
            Dispatch(() => session.Close());
            return true;
        }

        return false;
    }

    private bool IsForegroundConnectionUsingRemoteKeyboardMode()
    {
        var foreground = GetForegroundSession();
        return foreground is not null && foreground.UsesRemoteKeyboardMode();
    }

    private bool UsesRemoteKeyboardMode()
    {
        var mode = GetEffectiveKeyboardMode();
        return mode == KeyboardMode.OnRemoteComputer ||
               (mode == KeyboardMode.InFullScreenOnly && _rdpService.FullScreen);
    }

    private void CycleSessionWindow(FormRdpClient current)
    {
        var sessions = _sessionWindows
            .Where(window => !window.IsDisposed && !window._sessionClosing && !window._previewOnly &&
                            window._rdpService.IsConnected)
            .ToArray();
        if (sessions.Length < 2)
            return;

        var currentIndex = Array.IndexOf(sessions, current);
        var next = sessions[(currentIndex < 0 ? 0 : currentIndex + 1) % sessions.Length];
        ActivateSession(next);
    }

    internal int GetRemoteResizeHitTest(Point screenPoint)
    {
        if (!IsRemoteWindowResizable)
            return Interop.HTCLIENT;

        if (IsTopRightControlArea(screenPoint))
            return Interop.HTCLIENT;
        if (IsToolbarArea(screenPoint))
            return Interop.HTCLIENT;

        var bounds = RectangleToScreen(ClientRectangle);
        var onLeft = screenPoint.X >= bounds.Left && screenPoint.X < bounds.Left + ResizeGripSize;
        var onRight = screenPoint.X < bounds.Right && screenPoint.X >= bounds.Right - ResizeGripSize;
        var onBottom = screenPoint.Y < bounds.Bottom && screenPoint.Y >= bounds.Bottom - ResizeGripSize;
        if (onBottom && onLeft)
            return Interop.HTBOTTOMLEFT;
        if (onBottom && onRight)
            return Interop.HTBOTTOMRIGHT;
        return Interop.HTCLIENT;
    }

    internal bool IsTopRightControlArea(Point screenPoint)
    {
        var bounds = RectangleToScreen(ClientRectangle);
        var topBand = Math.Max(8, (int)Math.Round(DeviceDpi / 96F * 8F));
        var rightBand = topBand;
        return screenPoint.Y >= bounds.Top && screenPoint.Y < bounds.Top + topBand &&
               screenPoint.X >= bounds.Right - rightBand && screenPoint.X < bounds.Right;
    }

    internal void BeginRemoteWindowResize(int hitTest, Point? dragOrigin = null)
    {
        if (!IsRemoteWindowResizable || hitTest is not (Interop.HTBOTTOMLEFT or Interop.HTBOTTOMRIGHT))
            return;
        var startCursor = dragOrigin ?? Cursor.Position;
        if (IsToolbarArea(startCursor))
            return;
        EndRemoteWindowMove();
        // A no-activate grip can be clicked while another application is
        // foreground. Establish the host before taking drag capture.
        Activate();
        if (_manualResizeHitTest != Interop.HTCLIENT || _pendingResizeHitTest != Interop.HTCLIENT)
            EndRemoteWindowResize();
        _pendingResizeHitTest = hitTest;
        _manualResizeStartCursor = startCursor;
        if (_resizeGestureTimer == null)
        {
            _resizeGestureTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _resizeGestureTimer.Tick += (_, _) =>
            {
                if (!IsRemoteWindowResizable || (Control.MouseButtons & MouseButtons.Left) == 0)
                {
                    EndRemoteWindowResize();
                    return;
                }
                UpdateManualResize(Cursor.Position);
            };
        }
        _resizeGestureTimer.Start();
        // The original edge click is held by the host until the drag
        // threshold is crossed. Capturing the pointer here guarantees that a
        // release over the native RDP child still reaches the host cleanup
        // path; a click without movement is then immediately harmless.
        if (IsHandleCreated)
            WindowChromeInterop.SetCapture(Handle);
        // Wait for actual movement. A native grip supplies the exact down
        // position; comparing it to a stale Cursor.Position here can start
        // and cancel a gesture before its first mouse-move arrives.
    }

    private int _pendingResizeHitTest = Interop.HTCLIENT;
    private System.Windows.Forms.Timer? _resizeGestureTimer;

    internal bool TryHandleWindowResizeInput(ref Message message)
    {
        if (!IsRemoteWindowResizable ||
            (_pendingResizeHitTest == Interop.HTCLIENT && _manualResizeHitTest == Interop.HTCLIENT) ||
            message.Msg is not (Interop.WM_MOUSEMOVE or Interop.WM_LBUTTONUP or Interop.WM_NCLBUTTONUP))
            return false;
        var point = ScreenPointFromMessage(message);
        if (message.Msg != Interop.WM_NCLBUTTONUP &&
            !WindowChromeInterop.ClientToScreen(message.HWnd, ref point)) return false;
        // Use the captured message, not a later global cursor sample. The
        // same entry handles the form, AxHost and nested ActiveX HWNDs.
        UpdateManualResize(point);
        if (message.Msg is Interop.WM_LBUTTONUP or Interop.WM_NCLBUTTONUP)
            EndRemoteWindowResize();
        return true;
    }

    private void UpdateManualResize(Point cursor)
    {
        if (_pendingResizeHitTest != Interop.HTCLIENT && IsRemoteWindowResizable)
        {
            var threshold = SystemInformation.DragSize;
            var clickArea = new Rectangle(_manualResizeStartCursor.X - threshold.Width / 2,
                _manualResizeStartCursor.Y - threshold.Height / 2, threshold.Width, threshold.Height);
            if (clickArea.Contains(cursor)) return;
            var hit = _pendingResizeHitTest;
            _pendingResizeHitTest = Interop.HTCLIENT;
            if (WindowState == FormWindowState.Maximized)
            {
                var visibleBounds = Screen.FromHandle(Handle).WorkingArea;
                WindowState = FormWindowState.Normal;
                Bounds = visibleBounds;
            }
            _manualResizeHitTest = hit;
            _manualResizeStartBounds = Bounds;
            _rdpService.SetInteractiveResize(true);
        }
        if (_manualResizeHitTest == Interop.HTCLIENT || !IsRemoteWindowResizable)
            return;

        var deltaX = cursor.X - _manualResizeStartCursor.X;
        var deltaY = cursor.Y - _manualResizeStartCursor.Y;
        var bounds = _manualResizeStartBounds;
        const int minimumWidth = 320;
        const int minimumHeight = 240;

        var fromLeft = _manualResizeHitTest is Interop.HTLEFT or Interop.HTTOPLEFT or Interop.HTBOTTOMLEFT;
        var fromRight = _manualResizeHitTest is Interop.HTRIGHT or Interop.HTTOPRIGHT or Interop.HTBOTTOMRIGHT;
        var fromTop = _manualResizeHitTest is Interop.HTTOP or Interop.HTTOPLEFT or Interop.HTTOPRIGHT;
        var fromBottom = _manualResizeHitTest is Interop.HTBOTTOM or Interop.HTBOTTOMLEFT or Interop.HTBOTTOMRIGHT;
        // Independent axes: horizontal-only movement must not alter height,
        // and vertical-only movement must not alter width.
        var newWidth = Math.Max(minimumWidth, fromLeft ? bounds.Width - deltaX : fromRight ? bounds.Width + deltaX : bounds.Width);
        var newHeight = Math.Max(minimumHeight, fromTop ? bounds.Height - deltaY : fromBottom ? bounds.Height + deltaY : bounds.Height);
        var newLeft = fromLeft ? bounds.Right - newWidth : bounds.Left;
        var newTop = fromTop ? bounds.Bottom - newHeight : bounds.Top;
        bounds = new Rectangle(newLeft, newTop, newWidth, newHeight);

        Bounds = bounds;
        // The remote display update is intentionally sent only when the drag
        // ends. Keep the local ActiveX viewport in sync during the drag so the
        // old-size surface cannot leave a background frame around the picture.
        _rdpService.RefreshPictureLayout();
    }

    internal void EndRemoteWindowResize()
    {
        _resizeGestureTimer?.Stop();
        var wasResizing = _manualResizeHitTest != Interop.HTCLIENT;
        var wasPending = _pendingResizeHitTest != Interop.HTCLIENT;
        if (!wasResizing && !wasPending)
            return;

        _pendingResizeHitTest = Interop.HTCLIENT;
        _manualResizeHitTest = Interop.HTCLIENT;
        // The RDP ActiveX child can own capture for a normal remote click.
        // Only release capture that this form took for a real resize gesture.
        if (IsHandleCreated && WindowChromeInterop.GetCapture() == Handle)
            WindowChromeInterop.ReleaseCapture();
        if (wasResizing)
        {
            RememberManualWindowBounds();
            _rdpService.SetInteractiveResize(false);
            QueueRemoteResize();
        }
    }

    private void GlassChromeStrip_ManagementRequested(object? sender, EventArgs e)
        => ShowManagementWindow();

    private bool _showingManagementWindow;

    private void ShowManagementWindow()
    {
        var managementWindow = _managementWindow ?? this;
        if (managementWindow.IsDisposed || managementWindow.Disposing ||
            managementWindow._sessionClosing || managementWindow._showingManagementWindow)
            return;
        managementWindow._showingManagementWindow = true;
        try
        {
        if (managementWindow.WindowState == FormWindowState.Minimized)
            managementWindow.WindowState = FormWindowState.Normal;
        if (!managementWindow.Visible)
            managementWindow.Show();

        managementWindow.EnsureConsoleOnScreen();

        managementWindow.BringToFront();
        if (managementWindow.IsHandleCreated)
        {
            WindowChromeInterop.BringWindowToTop(managementWindow.Handle);
            WindowChromeInterop.SetForegroundWindow(managementWindow.Handle);
        }
        managementWindow.Activate();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            // Preserve the resident console and capture the actual failure
            // instead of letting a queued shortcut exception terminate it.
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "PureRDP-console.log"),
                $"{DateTime.Now:O} Console activation failed: {ex}{Environment.NewLine}"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        finally { managementWindow._showingManagementWindow = false; }
    }

    private void EnsureConsoleOnScreen()
    {
        if (_isSessionWindow) return;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        var area = Screen.FromControl(this).WorkingArea;
        var minimum = new Size(Math.Min(900, area.Width), Math.Min(600, area.Height));
        var invalidSize = Width < minimum.Width || Height < minimum.Height;
        if (invalidSize)
        {
            WindowState = FormWindowState.Normal;
            Size = new Size(Math.Min(1200, area.Width), Math.Min(800, area.Height));
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        }
        MinimumSize = minimum;
        // Maximized windows include invisible system borders outside the work
        // area. Preserve that state; normal windows must fit in full.
        if (WindowState == FormWindowState.Maximized) return;
        if (Screen.AllScreens.Any(screen => screen.WorkingArea.Contains(Bounds))) return;
        Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
        Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    private void GlassChromeStrip_MinimizeRequested(object? sender, EventArgs e)
        => (_managementWindow ?? this).ToggleAllSessionMinimize(queueOnly: false);

    private void GlassChromeStrip_MaximizeRequested(object? sender, EventArgs e)
    {
        if (_rdpService.IsConnected && _rdpService.FullScreen)
        {
            _rdpService.FullScreen = false;
            ApplyKeyboardModeForCurrentWindow();
            return;
        }

        if (WindowState == FormWindowState.Maximized)
        {
            var work = Screen.FromHandle(Handle).WorkingArea;
            WindowState = FormWindowState.Normal;
            // Auto-maximize is a startup preference. Windows may remember a
            // working-area-sized RestoreBounds, so explicitly restore a usable
            // normal window instead of appearing to remain maximized.
            ApplyConnectionWindowSize(forceNormal: true);
            Location = new Point(work.Left, work.Top);
        }
        else
            WindowState = FormWindowState.Maximized;
        ApplyKeyboardModeForCurrentWindow();
        _glassChromeStrip?.UpdateMaximizeGlyph(WindowState == FormWindowState.Maximized);
        PositionWindowChrome();
        QueueRemoteResize();
    }

    private void GlassChromeStrip_CloseRequested(object? sender, EventArgs e)
        => Close();

    private void InitializeConnectionLibrary()
    {
        foreach (Control control in panelStartup.Controls)
            control.Dispose();
        panelStartup.Controls.Clear();

        _connectionLibraryPanel = new ConnectionLibraryPanel();
        _connectionLibraryPanel.PreviewSourceProvider = GetPreviewSource;
        _connectionLibraryPanel.PreviewHostProvider = GetPreviewHost;
        _connectionLibraryPanel.ConnectionQualityProvider = GetConnectionQuality;
        _connectionLibraryPanel.StatusRefreshRequested = RefreshConsoleStatusAsync;
        _connectionLibraryPanel.PreviewToggledRequested += (settings, isOpen) =>
        {
            if (isOpen) StartPreview(settings);
            else StopPreview(settings);
        };
        _connectionLibraryPanel.ConnectRequested += ConnectionLibrary_ConnectRequested;
        _connectionLibraryPanel.EditRequested += ConnectionLibrary_EditRequested;
        _connectionLibraryPanel.DeleteRequested += ConnectionLibrary_DeleteRequested;
        _connectionLibraryPanel.NewConnectionRequested += ConnectionLibrary_NewConnectionRequested;
        _connectionLibraryPanel.GlobalSettingsRequested += ShowGlobalSettings;
        _connectionLibraryPanel.Load += (_, _) => UpdateAvailabilityScanDevices(scanNow: true);
        panelStartup.Controls.Add(_connectionLibraryPanel);
    }

    private async Task RefreshConsoleStatusAsync()
    {
        if (IsDisposed || _sessionClosing) return;

        // Keep cards and live RDP surfaces intact. The existing scanner already
        // owns the saved device list and serializes scans with periodic checks.
        var refreshes = new List<Task>();
        if (_availabilityScanner != null)
            refreshes.Add(_availabilityScanner.ScanNowAsync());
        foreach (var session in _sessionWindows.ToArray())
            if (!session.IsDisposed && !session._sessionClosing)
                refreshes.Add(session._rdpService.RefreshConnectionQualityAsync());
        foreach (var preview in _previewSessions.Values.ToArray())
            if (!preview.IsDisposed)
                refreshes.Add(preview.RefreshConnectionQualityAsync());
        await Task.WhenAll(refreshes);
    }

    private void AvailabilityScanner_StatusChanged(object? sender, DeviceAvailabilityChangedEventArgs e)
    {
        if (IsDisposed || _sessionClosing || _connectionLibraryPanel == null) return;
        try
        {
            if (InvokeRequired)
                BeginInvoke(new Action(() => _connectionLibraryPanel?.UpdateAvailability(e.Hostname, e.State)));
            else
                _connectionLibraryPanel.UpdateAvailability(e.Hostname, e.State);
        }
        catch (InvalidOperationException) { }
    }

    private void UpdateAvailabilityScanDevices(bool scanNow = false)
    {
        if (_availabilityScanner == null || _connectionLibraryPanel == null) return;
        _availabilityScanner.SetDevices(_connectionLibraryPanel.Connections);
        if (scanNow) _availabilityScanner.ScanNow();
    }

    private void ShowConnectionLibrary()
    {
        SetRemoteConnectionChrome(false);
        panelRdp.Visible = false;
        panelConnect.Visible = false;
        panelStartup.Visible = true;
        panelStartup.BringToFront();
        _connectionLibraryPanel?.Reload();
        UpdateAvailabilityScanDevices();
        UpdateWindowTitle();
    }

    private void ConnectionLibrary_ConnectRequested(ConnectionSettings settings)
    {
        _appSettings.Connection = settings.Clone();
        _appSettings.RdpFilePath = null;
        _appSettings.NoCloseOnDisconnect = true;
        _launchedFromLibrary = true;
        Connect();
    }

    private void ConnectionLibrary_NewConnectionRequested()
    {
        var previousSettings = _appSettings;
        _appSettings = new ApplicationSettings { NoCloseOnDisconnect = true };

        if (!ShowSettings())
        {
            _appSettings = previousSettings;
            return;
        }

        if (_appSettings.Connection.IsValid())
        {
            _connectionLibraryPanel?.Reload();
            UpdateAvailabilityScanDevices(scanNow: true);
            if (_settingsConnectRequested) Connect();
        }
    }

    private void ConnectionLibrary_EditRequested(ConnectionSettings settings)
    {
        var previousSettings = _appSettings;
        var previousHostname = settings.Hostname;
        _appSettings = new ApplicationSettings
        {
            Connection = settings.Clone(),
            NoCloseOnDisconnect = true,
        };

        if (!ShowSettings())
        {
            _appSettings = previousSettings;
            return;
        }

        if (_appSettings.Connection.IsValid())
        {
            if (!string.Equals(previousHostname, _appSettings.Connection.Hostname, StringComparison.OrdinalIgnoreCase))
                RecentConnectionsService.Remove(previousHostname);

            // ShowSettings already updates the edited card in place.  Do not
            // reload the whole library here: that tears down every card and
            // causes the console to flash once after saving.
            UpdateAvailabilityScanDevices(scanNow: true);
            if (_settingsConnectRequested) Connect();
        }
    }

    private void ConnectionLibrary_DeleteRequested(ConnectionSettings settings)
    {
        var hostname = settings.Hostname ?? string.Empty;
        if (string.IsNullOrWhiteSpace(hostname))
            return;

        var result = MessageBox.Show(
            this,
            $"确定要删除“{hostname}”吗？\r\n\r\n这只会从 PureRDP 的设备列表中移除，不会删除其他文件。",
            "删除设备",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (result != DialogResult.Yes)
            return;

        StopPreview(settings);
        RecentConnectionsService.Remove(hostname);
        _connectionLibraryPanel?.Reload();
        UpdateAvailabilityScanDevices(scanNow: true);
    }
    private void Disconnect()
    {
        if (!_isSessionWindow && _sessionWindow is { IsDisposed: false })
        {
            _sessionWindow.Disconnect();
            return;
        }

        StopAutomaticReconnect();
        HideConnectingMessage();
        _rdpService.Disconnect();
    }

    private void ShowConnectingMessage(string message)
    {
        // Keep the management window's native title bar while a connection is
        // being negotiated. The glass controls belong to the connected canvas.
        SetRemoteConnectionChrome(false);
        panelStartup.Visible = false;
        // The RDP ActiveX control must be created under a visible native
        // parent. Keep its host visible during initialization; panelConnect
        // is brought to the front below, so the user still sees the connecting
        // surface while the control is being created.
        panelRdp.Visible = true;
        panelConnect.Visible = true;
        panelConnect.BringToFront();
        _connectionLoader.SetLoading(true);

        lbConnecting.Text = message;
        lbConnecting.Visible = true;
        btnCancel.Visible = true;
        LayoutConnectingSurface();
    }

    private void ConnectingSurface_Resize(object? sender, EventArgs e) => LayoutConnectingSurface();

    private void LayoutConnectingSurface()
    {
        var dpi = DeviceDpi / 96f;
        int Px(int value) => (int)Math.Round(value * dpi);
        var width = panelConnect.ClientSize.Width;
        var height = panelConnect.ClientSize.Height;
        var loaderSize = Math.Max(Px(60), Math.Min(Px(200), height - Px(110)));
        var top = Math.Max(Px(8), (height - loaderSize - Px(92)) / 2);
        _connectionLoader.SetBounds((width - loaderSize) / 2, top, loaderSize, loaderSize);
        lbConnecting.SetBounds(Px(16), top + loaderSize, Math.Max(1, width - Px(32)), Px(36));
        btnCancel.SetBounds((width - Px(112)) / 2, top + loaderSize + Px(48), Px(112), Px(36));
    }

    private void StartConnection()
        => StartConnection(automaticRetry: false);

    private void StartConnection(bool automaticRetry)
    {
        if (automaticRetry)
            _reconnectDialog?.ShowConnecting(_automaticReconnectAttempts);
        else
            ShowConnectingMessage($"正在连接 {_appSettings.Connection.Hostname}…");

        try
        {
            ApplyKeyboardModeForCurrentWindow();
            _rdpService.Connect(_appSettings.Connection);
            if (automaticRetry && _reconnectFullScreen && !_rdpService.FullScreen)
                _rdpService.FullScreen = true;
            if (_restoreFullScreenOnConnect)
            {
                if (!_rdpService.FullScreen)
                    _rdpService.FullScreen = true;
                _restoreFullScreenOnConnect = false;
            }
        }
        catch (Exception exception)
        {
            if (automaticRetry)
            {
                _automaticReconnectAttemptInProgress = false;
                ScheduleAutomaticReconnect(exception.Message);
            }
            else if (_isSessionWindow && !_previewOnly && !_sessionClosing)
            {
                BeginAutomaticReconnect(exception.Message);
            }
            else
            {
                HandleConnectionStartFailure(exception);
            }
        }
    }

    private void BeginAutomaticReconnect(string? detail)
    {
        if (!_isSessionWindow || _previewOnly || _sessionClosing || IsDisposed)
            return;

        if (!_automaticReconnectActive)
        {
            _automaticReconnectActive = true;
            _automaticReconnectAttempts = 0;
            _automaticReconnectAttemptInProgress = false;
            HideConnectingMessage();

            _reconnectDialog = new RdpReconnectDialog(_appSettings.Connection.Hostname ?? "远程计算机");
            _reconnectDialog.RetryRequested += ReconnectDialog_RetryRequested;
            _reconnectDialog.CancelRequested += ReconnectDialog_CancelRequested;
            _reconnectDialog.Show(this);
        }

        ScheduleAutomaticReconnect(detail);
    }

    private void ScheduleAutomaticReconnect(string? detail)
    {
        if (!_automaticReconnectActive || _sessionClosing || IsDisposed)
            return;

        if (_automaticReconnectAttempts >= MaxAutomaticReconnectAttempts)
        {
            _managementWindow?._connectionLibraryPanel?.ShowStatus(
                $"{_appSettings.Connection.Hostname} 自动重试 {MaxAutomaticReconnectAttempts} 次仍未连接，已关闭连接窗口。");
            StopAutomaticReconnect();
            _sessionClosing = true;
            if (IsHandleCreated)
                BeginInvoke(new Action(() =>
                {
                    if (!IsDisposed) Close();
                }));
            else if (!IsDisposed)
                Close();
            return;
        }

        var nextAttempt = _automaticReconnectAttempts + 1;
        var delaySeconds = 1 << nextAttempt;
        _reconnectDialog?.WaitForRetry(nextAttempt, delaySeconds, detail);
    }

    private void ReconnectDialog_RetryRequested(int attempt)
    {
        if (!_automaticReconnectActive || _sessionClosing || IsDisposed ||
            attempt != _automaticReconnectAttempts + 1 || attempt > MaxAutomaticReconnectAttempts)
            return;

        _automaticReconnectAttempts = attempt;
        _automaticReconnectAttemptInProgress = true;
        StartConnection(automaticRetry: true);
    }

    private void ReconnectDialog_CancelRequested(object? sender, EventArgs e)
    {
        StopAutomaticReconnect();
        if (_sessionClosing || IsDisposed)
            return;

        _sessionClosing = true;
        Close();
    }

    private void StopAutomaticReconnect()
    {
        _automaticReconnectActive = false;
        _automaticReconnectAttempts = 0;
        _automaticReconnectAttemptInProgress = false;
        var dialog = _reconnectDialog;
        _reconnectDialog = null;
        if (dialog == null) return;

        dialog.RetryRequested -= ReconnectDialog_RetryRequested;
        dialog.CancelRequested -= ReconnectDialog_CancelRequested;
        dialog.CloseFromOwner();
        dialog.Dispose();
    }

    private void HandleConnectionStartFailure(Exception exception)
    {
        var detail = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;
        var message = $"连接失败：{detail}";

        if (_isSessionWindow)
        {
            if (_previewOnly)
            {
                _sessionClosing = true;
                if (!IsDisposed && IsHandleCreated)
                    BeginInvoke(Close);
                else if (!IsDisposed)
                    Close();
                return;
            }
            // Keep the management window alive and leave the reason in its
            // library instead of allowing an ActiveX initialization error to
            // terminate the process or leave only a flashing session window.
            _managementWindow?.Show();
            _managementWindow?.Activate();
            _managementWindow?._connectionLibraryPanel?.ShowStatus(message);

            _sessionClosing = true;
            if (!IsDisposed && IsHandleCreated)
                BeginInvoke(Close);
            else if (!IsDisposed)
                Close();
        }
        else
        {
            ShowConnectionLibrary();
            _connectionLibraryPanel?.ShowStatus(message);
        }
    }

    private void HideConnectingMessage()
    {
        _connectionLoader.SetLoading(false);
        panelConnect.Visible = false;
        lbConnecting.Visible = false;
        btnCancel.Visible = false;
    }

    private void ToggleFullScreen()
    {
        // Grid mode owns the bounds of every active session.  A toolbar or
        // system-menu fullscreen request must not move one tile out of the
        // mosaic and cover the other sessions.
        if (_isSessionWindow && IsGridLayoutActive)
        {
            _managementWindow!.ArrangeSessions();
            return;
        }

        if (!_isSessionWindow && _sessionWindow is { IsDisposed: false })
        {
            _sessionWindow.ToggleFullScreen();
            return;
        }

        var previousFullScreen = _rdpService.FullScreen;
        _rdpService.FullScreen = !previousFullScreen;
        if (_rdpService.FullScreen != previousFullScreen && !IsDisposed &&
            !_sessionClosing && IsHandleCreated && WindowState != FormWindowState.Minimized)
        {
            // The control strip is deliberately non-activating. A manual
            // fullscreen toggle still needs to bring its owning connection
            // window forward, otherwise only the owned strip may remain over
            // another application's window while the RDP form changes behind it.
            BringToFront();
            WindowChromeInterop.BringWindowToTop(Handle);
            WindowChromeInterop.SetForegroundWindowReliable(Handle);
        }
    }

    private bool ShowSettings()
    {
        _settingsConnectRequested = false;
        var previousHostname = _appSettings.Connection.Hostname;
        var currentRdpSize = _sessionWindow is { IsDisposed: false }
            ? _sessionWindow.ClientSize
            : panelRdp.Size;
        using var settingsDialog = new FormSettings(_appSettings, Icon, currentRdpSize);
        var dialogConfirmed = settingsDialog.ShowDialog(this) == DialogResult.OK;
        if (!dialogConfirmed)
            return false;

        _appSettings = settingsDialog.UpdatedSettings;
        _settingsConnectRequested = settingsDialog.ConnectRequested;
        _formSystemMenuService.UpdateZoomCheckmarks(_appSettings.Connection.ScaleFactor);
        _formSizeEnforcementService.IsActive = _appSettings.Connection.AutoResize == true ||
                                               _appSettings.Connection.AutoMaximizeWindow == true;

        if (_settingsConnectRequested && _isSessionWindow)
            ApplyConnectionSettings(_appSettings.Connection);
        else if (_settingsConnectRequested)
        {
            var target = _sessionWindows.FirstOrDefault(s => !s.IsDisposed && !s._sessionClosing &&
                string.Equals(s._appSettings.Connection.Hostname, _appSettings.Connection.Hostname, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s._appSettings.Connection.Username, _appSettings.Connection.Username, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s._appSettings.Connection.Domain, _appSettings.Connection.Domain, StringComparison.OrdinalIgnoreCase));
            target?.ApplyConnectionSettings(_appSettings.Connection);
        }

        // A saved device edit is already persisted by FormSettings. Update
        // only its existing card so the console does not tear down and rebuild
        // every card, which caused a visible flash and lost transient state.
        if (_connectionLibraryPanel != null &&
            !_connectionLibraryPanel.UpdateConnection(_appSettings.Connection, previousHostname))
        {
            // No matching card means this was a new command-line/profile
            // connection; only that case needs a full library reload.
            if (_appSettings.Connection.IsValid())
                _connectionLibraryPanel.Reload();
        }
        UpdateAvailabilityScanDevices(scanNow: true);

        UpdateWindowTitle();
        UpdateBadgeIcon();

        return true;
    }

    private void ApplyKeyboardModeForCurrentWindow()
    {
        if (!_isSessionWindow || _rdpService == null)
            return;

        var effectiveMode = GetEffectiveKeyboardMode();
        if (_appliedKeyboardMode == effectiveMode)
            return;

        _rdpService.SetKeyboardHookMode(effectiveMode);
        _appliedKeyboardMode = effectiveMode;
        (_managementWindow ?? this)._globalShortcutFilter?.RefreshHook();
    }

    internal bool IsRemoteInputWindow(IntPtr handle)
        => handle != IntPtr.Zero && _sessionWindows.Any(session =>
            !session.IsDisposed && !session._sessionClosing && !session._previewOnly &&
            session.panelRdp.IsHandleCreated &&
            (handle == session.panelRdp.Handle ||
             WindowChromeInterop.IsChild(session.panelRdp.Handle, handle)));

    private void RefreshGlobalShortcutHookOrder()
    {
        var filter = (_managementWindow ?? this)._globalShortcutFilter;
        filter?.RefreshHook();
        // Fullscreen and native child-focus changes can install the RDP hook
        // after our synchronous refresh. Rejoin once the current dispatch has
        // finished, retaining the in-progress chord and repeat latch.
        filter?.QueueRemoteInputHookRefresh();
    }

    private KeyboardMode GetEffectiveKeyboardMode()
    {
        var settings = _appSettings.Connection;
        var mode = settings.KeyboardModeFullscreen;
        if (!_rdpService.FullScreen && settings.ScreenMode != ScreenMode.FullScreen &&
            settings.UseAllMonitors != true)
        {
            mode = WindowState == FormWindowState.Maximized
                ? settings.KeyboardModeWindowFullscreen
                : settings.KeyboardModeWindowed;
        }
        // GlobalShortcutEffectMode only controls interception of PureRDP's
        // configured shortcuts. It must never rewrite the RDP session's
        // keyboard forwarding mode; ordinary Windows key combinations still
        // follow the per-device setting.
        return mode ?? KeyboardMode.OnLocalComputer;
    }

    private void ApplyConnectionSettings(ConnectionSettings settings)
    {
        _appSettings.Connection = settings.Clone();
        _formSystemMenuService.UpdateZoomCheckmarks(_appSettings.Connection.ScaleFactor);
        _formSizeEnforcementService.IsActive = _appSettings.Connection.AutoResize == true ||
                                               _appSettings.Connection.AutoMaximizeWindow == true;
        if (IsGridLayoutActive)
            _rdpService.FullScreen = false;
        else
            _rdpService.FullScreen = _appSettings.Connection.ScreenMode == ScreenMode.FullScreen;
        if (_isSessionWindow)
        {
            if (ShouldAutoMaximizeWindow)
                WindowState = FormWindowState.Maximized;
            else
            {
                if (WindowState == FormWindowState.Maximized)
                    WindowState = FormWindowState.Normal;
                ApplyConnectionWindowSize();
            }
        }
        // The selected keyboard mode depends on the actual window/RDP
        // fullscreen state, so apply it after the state above is settled.
        ApplyKeyboardModeForCurrentWindow();
        _rdpService.Reconnect(_appSettings.Connection);
        UpdateWindowTitle();
        UpdateBadgeIcon();
    }

    private void ShowDisconnectStatus(DisconnectedEventArgs e)
    {
        var message = $"""
            连接已断开：{e.ReasonCode:G} ({e.ReasonCode:D}) / {e.ExtendedReasonCode:G} ({e.ExtendedReasonCode:D})

            {e.ErrorDescription}
            """;

        _connectionLibraryPanel?.ShowStatus(e.IsIntentionalDisconnect ? null : message.Trim());
    }
    private void LoadAndApplyWindowSettings()
    {
        var hostname = _appSettings.Connection.Hostname;
        var persistedSettings = WindowPersistenceService.Load(_isSessionWindow ? hostname : null);
        ApplyPersistedWindowSettings(persistedSettings);
    }

    private void ApplyPersistedWindowSettings(WindowSettings? persistedSettings)
    {
        _appSettings.Window.MergeFrom(persistedSettings);
        this.ApplyWindowSettings(_appSettings.Window);
        if (!_isSessionWindow) EnsureConsoleOnScreen();
        _restoreFullScreenOnConnect = _isSessionWindow && !ShouldAutoMaximizeWindow &&
                                      _appSettings.Window.FullScreen == true;
        _fullScreenMultiMonitor = _appSettings.Window.FullScreenMultiMonitor == true;
        _reconnectFullScreen = _restoreFullScreenOnConnect;
        _reconnectFullScreenMultiMonitor = _fullScreenMultiMonitor;

        _applyConnectionWindowSizeAfterConnect = false;
        if (_isSessionWindow && ShouldAutoMaximizeWindow)
        {
            // WindowState.Maximized uses the monitor working area, so the
            // taskbar remains visible. This is deliberately not RDP full
            // screen, which uses the entire monitor bounds.
            WindowState = FormWindowState.Maximized;
        }
        else if (_isSessionWindow && TryRestoreManualWindowBounds())
            return;
        else if (_isSessionWindow && HasConnectionWindowSize)
        {
            // Profile dimensions initialize windows that have not yet been
            // manually moved/resized. Explicit user geometry takes priority.
            _applyConnectionWindowSizeAfterConnect = true;
            ApplyConnectionWindowSize();
        }
    }

    private bool HasConnectionWindowSize
        => _appSettings.Connection.ScreenMode != ScreenMode.FullScreen &&
           _appSettings.Connection.UseAllMonitors != true &&
           _appSettings.Connection.AutoMaximizeWindow != true &&
           (_appSettings.Connection.Width.HasValue || _appSettings.Connection.Height.HasValue ||
            _appSettings.Connection.ViewScaleFactor is > 0);

    private bool ShouldAutoMaximizeWindow
        => _isSessionWindow &&
           _appSettings.Connection.ScreenMode != ScreenMode.FullScreen &&
           _appSettings.Connection.UseAllMonitors != true &&
           _appSettings.Connection.AutoMaximizeWindow == true;

    private void ApplyConnectionWindowSize(bool forceNormal = false)
    {
        if (TryRestoreManualWindowBounds()) return;
        if (!forceNormal && !HasConnectionWindowSize)
            return;

        var desktop = _rdpService.PictureDesktopSize;
        var width = _appSettings.Connection.Width ?? (desktop.Width > 0 ? desktop.Width : 1200);
        var height = _appSettings.Connection.Height ?? (desktop.Height > 0 ? desktop.Height : 800);
        var scale = _appSettings.Connection.ViewScaleFactor ?? 0;
        var work = Screen.FromHandle(Handle).WorkingArea;
        var availableWidth = Math.Max(320, work.Width - 80);
        var availableHeight = Math.Max(240, work.Height - 80);
        var ratio = scale == 0 ? Math.Min(1.0, Math.Min((double)availableWidth / width, (double)availableHeight / height)) : scale / 100.0;
        ratio = Math.Min(ratio, Math.Min((double)availableWidth / width, (double)availableHeight / height));
        ClientSize = new Size(Math.Max(1, (int)Math.Floor(width * ratio)),
            Math.Max(1, (int)Math.Floor(height * ratio)));
    }

    private void UpdateWindowTitle()
    {
        var title = "PureRDP";
        if (!string.IsNullOrEmpty(_appSettings.RdpFilePath))
            title = $"({Path.GetFileNameWithoutExtension(_appSettings.RdpFilePath)}) - {title}";
        if (!string.IsNullOrEmpty(_appSettings.Connection.Hostname))
            title = $"{_appSettings.Connection.DisplayName ?? _appSettings.Connection.Hostname} - {title}";
        Text = title;
    }

    private void RememberManualWindowBounds()
    {
        if (!_isSessionWindow || _previewOnly || _rdpService.FullScreen ||
            WindowState != FormWindowState.Normal || Bounds.Width < 320 || Bounds.Height < 240)
            return;
        var settings = _appSettings.Window;
        settings.ManualWindowX = Left;
        settings.ManualWindowY = Top;
        settings.ManualWindowWidth = Width;
        settings.ManualWindowHeight = Height;
        _applyConnectionWindowSizeAfterConnect = false;
        if (_independentBounds.HasValue)
        {
            _independentBounds = Bounds;
            _independentState = FormWindowState.Normal;
            _independentFullScreen = false;
        }
    }

    private bool TryRestoreManualWindowBounds()
    {
        var settings = _appSettings.Window;
        if (!_isSessionWindow || _previewOnly || _rdpService.FullScreen || settings.ManualWindowX is not int x || settings.ManualWindowY is not int y ||
            settings.ManualWindowWidth is not >= 320 || settings.ManualWindowHeight is not >= 240)
            return false;
        var bounds = new Rectangle(x, y, settings.ManualWindowWidth.Value, settings.ManualWindowHeight.Value);
        // A disconnected monitor must not make the remembered window
        // unreachable. Keep the original geometry when it still fits.
        var screen = Screen.AllScreens.FirstOrDefault(item => item.WorkingArea.IntersectsWith(bounds))
            ?? Screen.FromControl(_managementWindow ?? this);
        var work = screen.WorkingArea;
        bounds.Size = new Size(Math.Min(bounds.Width, work.Width), Math.Min(bounds.Height, work.Height));
        bounds.Location = new Point(Math.Clamp(bounds.X, work.Left, work.Right - bounds.Width),
            Math.Clamp(bounds.Y, work.Top, work.Bottom - bounds.Height));
        WindowState = FormWindowState.Normal;
        Bounds = bounds;
        return true;
    }

    private WindowSettings CaptureWindowSettings()
    {
        var windowSettings = this.GetWindowSettings();
        if (_rdpService.FullScreen && _nonFullScreenBounds.Width > 0 && _nonFullScreenBounds.Height > 0)
        {
            windowSettings.WindowX = _nonFullScreenBounds.X;
            windowSettings.WindowY = _nonFullScreenBounds.Y;
            windowSettings.WindowWidth = _nonFullScreenBounds.Width;
            windowSettings.WindowHeight = _nonFullScreenBounds.Height;
            windowSettings.Maximized = _nonFullScreenState == FormWindowState.Maximized;
        }
        windowSettings.FullScreen = _isSessionWindow ? _rdpService.FullScreen : null;
        windowSettings.FullScreenMultiMonitor = _rdpService.FullScreen ? _fullScreenMultiMonitor : null;
        if (_independentBounds is Rectangle independent)
        {
            windowSettings.WindowX = independent.X;
            windowSettings.WindowY = independent.Y;
            windowSettings.WindowWidth = independent.Width;
            windowSettings.WindowHeight = independent.Height;
            windowSettings.Maximized = _independentState == FormWindowState.Maximized;
            windowSettings.FullScreen = _independentFullScreen;
        }
        windowSettings.ManualWindowX = _appSettings.Window.ManualWindowX;
        windowSettings.ManualWindowY = _appSettings.Window.ManualWindowY;
        windowSettings.ManualWindowWidth = _appSettings.Window.ManualWindowWidth;
        windowSettings.ManualWindowHeight = _appSettings.Window.ManualWindowHeight;
        return windowSettings;
    }

    private void SaveWindowSettings()
    {
        var hostname = _isSessionWindow ? _appSettings.Connection.Hostname : null;
        CaptureWindowSettings().Save(hostname);
    }

    private void SetZoomLevel(uint zoomPercent)
    {
        if (!_isSessionWindow && _sessionWindow is { IsDisposed: false })
        {
            _appSettings.Connection.ScaleFactor = zoomPercent;
            _sessionWindow.SetZoomLevel(zoomPercent);
            _formSystemMenuService.UpdateZoomCheckmarks(zoomPercent);
            return;
        }

        _appSettings.Connection.ScaleFactor = zoomPercent;
        _rdpService.Reconnect(_appSettings.Connection);
        _formSystemMenuService.UpdateZoomCheckmarks(_appSettings.Connection.ScaleFactor);
    }

    private void SetFullScreenMode(bool isFullScreen, bool isMultiMonitor)
    {
        if (_sessionClosing || Disposing || IsDisposed)
            return;
        if (isFullScreen) EndRemoteWindowMove();
        ResumeDisplayAfterExternalRestore();
        if (isFullScreen && IsGridLayoutActive)
        {
            // The RDP control can report a fullscreen request during connect
            // or reconnect even after the grid has taken ownership.  Defer
            // the corrective transition until this event has finished so the
            // form has valid saved bounds for the normal state.
            if (IsHandleCreated)
            {
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (!IsDisposed && !_sessionClosing && _rdpService.FullScreen)
                            _rdpService.FullScreen = false;
                        ApplyKeyboardModeForCurrentWindow();
                        RefreshGlobalShortcutHookOrder();
                        _managementWindow?.ArrangeSessions();
                    }));
                }
                catch (InvalidOperationException) { }
            }
            return;
        }

        // The service sets FullScreen before raising this callback. During a
        // maximize-to-fullscreen transition, changing WindowState to Normal
        // raises Resize synchronously. That handler used to apply Screen.Bounds
        // before the normal return bounds had been saved, so the window could
        // get stuck at an intermediate size and leave a black taskbar strip.
        if (_fullScreenTransitionInProgress)
            return;

        _fullScreenTransitionInProgress = true;
        var previousRestoreState = _restoringControlBarState;
        var resizeDetectionWasEnabled = _formResizeDetectionService.Enabled;
        var displayUpdatesWereSuspended = _rdpService.DisplayUpdatesSuspended;
        _restoringControlBarState = true;
        _formResizeDetectionService.Enabled = false;
        if (!displayUpdatesWereSuspended)
            _rdpService.SetDisplayUpdatesSuspended(true, refreshOnResume: false);

        try
        {
            _fullScreenMultiMonitor = isMultiMonitor;
            if (isFullScreen)
            {
                // Capture the return geometry before changing WindowState:
                // RestoreBounds is the actual normal window size while
                // maximized, whereas Bounds may already include the work area.
                var stateBeforeTransition = WindowState;
                _nonFullScreenState = stateBeforeTransition == FormWindowState.Minimized
                    ? (_hasLastVisibleWindowState ? _lastVisibleWindowState : FormWindowState.Normal)
                    : stateBeforeTransition;
                _nonFullScreenBounds = stateBeforeTransition switch
                {
                    FormWindowState.Maximized => RestoreBounds,
                    FormWindowState.Minimized when _hasLastVisibleWindowState => _lastVisibleWindowBounds,
                    _ => Bounds,
                };
                if (_nonFullScreenBounds.Width <= 0 || _nonFullScreenBounds.Height <= 0)
                    _nonFullScreenBounds = Bounds;

                if (WindowState is FormWindowState.Maximized or FormWindowState.Minimized)
                    WindowState = FormWindowState.Normal;

                // Full screen is a state of the connected RDP surface. Do not
                // change the management window into a borderless shell here.
                ApplyRemoteWindowChrome();
                Bounds = GetTotalMonitorBounds(isMultiMonitor);
                _glassChromeStrip?.UpdateMaximizeGlyph(true);
                PositionWindowChrome();
            }
            else
            {
                if (_nonFullScreenBounds.Width > 0 && _nonFullScreenBounds.Height > 0)
                    Bounds = _nonFullScreenBounds;
                WindowState = _nonFullScreenState;
                if (_remoteChromeActive)
                    ApplyRemoteWindowChrome();
                else
                    ApplyNativeWindowChrome();
                _glassChromeStrip?.UpdateMaximizeGlyph(WindowState == FormWindowState.Maximized);
                PositionWindowChrome();
            }
        }
        finally
        {
            _formResizeDetectionService.Enabled = resizeDetectionWasEnabled;
            if (!displayUpdatesWereSuspended)
                _rdpService.SetDisplayUpdatesSuspended(false, refreshOnResume: false);
            _restoringControlBarState = previousRestoreState;
            _fullScreenTransitionInProgress = false;
        }
        ResumeDisplayAfterExternalRestore();
        ConnectionLogService.WriteDiagnostic($"fullscreen-transition target={isFullScreen} host={ClientSize.Width}x{ClientSize.Height} canvas={panelRdp.ClientSize.Width}x{panelRdp.ClientSize.Height} display-paused={_rdpService.DisplayUpdatesSuspended}");
        // Resolve the mode after the actual RDP fullscreen state and host
        // window state have been updated. This is especially important for
        // the per-device "remote computer" choice and InFullScreenOnly.
        ApplyKeyboardModeForCurrentWindow();
        RefreshGlobalShortcutHookOrder();
    }

    private Rectangle GetTotalMonitorBounds(bool useAllMonitors)
    {
        if (!useAllMonitors)
        {
            var screen = Screen.FromControl(this);
            return screen.Bounds;
        }

        var screens = Screen.AllScreens;
        if (screens.Length == 0)
            return Screen.PrimaryScreen?.Bounds ?? new Rectangle();

        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;

        foreach (var screen in screens)
        {
            if (screen.Bounds.X < minX)
                minX = screen.Bounds.X;
            if (screen.Bounds.Y < minY)
                minY = screen.Bounds.Y;
            if (screen.Bounds.Right > maxX)
                maxX = screen.Bounds.Right;
            if (screen.Bounds.Bottom > maxY)
                maxY = screen.Bounds.Bottom;
        }

        return new Rectangle(minX, minY, maxX - minX, maxY - minY);
    }

    private bool IsGridLayoutActive
        => _isSessionWindow && _managementWindow is { IsDisposed: false, _autoTileSessions: true } &&
           _managementWindow._sessionWindows.Count(session =>
               !session.IsDisposed && !session._sessionClosing && !session._previewOnly) >= 2;

    private void UpdateBadgeIcon()
    {
        const int size = 14;
        var x = Properties.Resources.AppIcon.Size.Width - size;
        var color = DeviceAccentColors.Resolve(_appSettings.Connection);
        Icon = _appSettings.Connection.Hostname.GenerateBadgeIcon(x, y: 0, size, size, color);
    }

    /// <summary>
    /// Called after the form handle has been created
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        _formResizeDetectionService.AttachToWindow(Handle);
        // Even-pixel enforcement belongs to RDP surfaces, not the console.
        // Its deferred resizes can replay transient startup/minimized bounds.
        if (_isSessionWindow) _formSizeEnforcementService.AttachToWindow(this);
        _formSystemMenuService.AttachToWindow(this);
        _formSystemMenuService.UpdateZoomCheckmarks(_appSettings.Connection.ScaleFactor);
    }

    protected override void SetVisibleCore(bool value)
    {
        if (value && _suppressInitialConsoleVisibility)
        {
            // Application.Run normally shows its main Form. Suppress that
            // first show before Windows sets WS_VISIBLE: hiding in Shown or
            // BeginInvoke is already one painted frame too late. The HWND,
            // tray icon and UI message loop remain alive for an explicit
            // tray click or a normal second launch to open the console.
            _suppressInitialConsoleVisibility = false;
            base.SetVisibleCore(false);
            return;
        }
        base.SetVisibleCore(value);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((_windowMovePending || _windowMoveActive) && keyData == Keys.Escape)
        {
            EndRemoteWindowMove();
            return true;
        }
        var escapeKeyPressed = keyData == Keys.Escape;
        var connectionActive = _rdpService.IsConnected;
        if (!escapeKeyPressed || connectionActive)
            return base.ProcessCmdKey(ref msg, keyData);

        Close();
        return true;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            // Keep shell minimize support without adding a visible frame.
            // Win+M skips windows that do not advertise WS_MINIMIZEBOX.
            if (_isSessionWindow || FormBorderStyle == FormBorderStyle.None)
                parameters.Style |= 0x00020000 | 0x00080000; // WS_MINIMIZEBOX | WS_SYSMENU
            if (_previewOnly)
            {
                parameters.ExStyle |= 0x00000080 | 0x08000000; // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                parameters.ExStyle &= ~0x00040000; // WS_EX_APPWINDOW
            }
            if (!_isSessionWindow && !_previewOnly)
                parameters.ExStyle |= 0x02000000; // WS_EX_COMPOSITED
            return parameters;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (TryHandleWindowMoveInput(ref m)) return;
        if (TryHandleWindowResizeInput(ref m)) return;
        if (m.Msg is 0x0215 or Interop.WM_CANCELMODE && (_windowMovePending || _windowMoveActive))
            EndRemoteWindowMove();
        if (!_isSessionWindow && _autoTileSessions && IsHandleCreated &&
            !_sessionClosing && m.Msg is 0x007E or 0x001A) // display/work-area change
        {
            BeginInvoke(new Action(ArrangeSessions));
        }
        if (m.Msg == Interop.WM_NCHITTEST && _remoteChromeActive &&
            !(_rdpService?.FullScreen ?? false) && IsTopRightControlArea(ScreenPointFromMessage(m)))
        {
            // Keep only the upper-right corner inert. The other three corner
            // grips continue to use the normal proportional resize path.
            m.Result = (IntPtr)Interop.HTCLIENT;
            return;
        }
        if (m.Msg == 0x0024 && _remoteChromeActive && FormBorderStyle == FormBorderStyle.None &&
            !(_rdpService?.FullScreen ?? false)) // WM_GETMINMAXINFO
        {
            base.WndProc(ref m);
            var screen = Screen.FromHandle(Handle);
            var info = Marshal.PtrToStructure<BorderlessMinMaxInfo>(m.LParam);
            info.MaxPosition = new Point(screen.WorkingArea.Left - screen.Bounds.Left,
                screen.WorkingArea.Top - screen.Bounds.Top);
            info.MaxSize = screen.WorkingArea.Size;
            Marshal.StructureToPtr(info, m.LParam, false);
            return;
        }
        if (m.Msg == 0x0215 &&
            (_manualResizeHitTest != Interop.HTCLIENT || _pendingResizeHitTest != Interop.HTCLIENT))
        {
            // A child ActiveX window can take and then lose mouse capture
            // without forwarding the release to the host. Do not leave the
            // resize state armed; it makes later remote clicks look like
            // system-beep-only input until the form is minimized/restored.
            EndRemoteWindowResize();
            return;
        }
        if (m.Msg == Interop.WM_CANCELMODE &&
            (_manualResizeHitTest != Interop.HTCLIENT || _pendingResizeHitTest != Interop.HTCLIENT))
        {
            EndRemoteWindowResize();
            return;
        }
        if (m.Msg == 0x0231) _rdpService?.SetInteractiveResize(true); // WM_ENTERSIZEMOVE
        if (m.Msg == 0x0232) _rdpService?.SetInteractiveResize(false); // WM_EXITSIZEMOVE

        var shiftDown = ModifierKeys.HasFlag(Keys.Shift);
        var isFullScreen = _appSettings.Connection.ScreenMode == ScreenMode.FullScreen;
        var useAllMonitors = _appSettings.Connection.UseAllMonitors == true;
        var autoResizeIsActive = _appSettings.Connection.AutoResize == true;

        switch (m.Msg)
        {
            case Interop.WM_NCHITTEST when IsBorderlessWindowed:
            {
                // Edge input is handled by RemoteResizeInputFilter. Never
                // enter DefWindowProc's modal sizing loop at the same time.
                m.Result = (IntPtr)Interop.HTCLIENT;
                return;
            }

            case Interop.WM_SYSCOMMAND:
                switch (m.WParam)
                {
                    case Interop.SC_MAXIMIZE when WindowState != FormWindowState.Minimized &&
                        (shiftDown || isFullScreen || useAllMonitors):
                        ToggleFullScreen();
                        return;
                }
                break;

            case Interop.WM_NCLBUTTONDBLCLK:
                switch (m.WParam)
                {
                    case Interop.HTCAPTION:
                        // Fullscreen is explicitly owned by the control bar's
                        // maximize-button double click. Do not let a blank
                        // borderless/caption area invoke the old behavior or
                        // fall through to the native default handler.
                        return;
                    case Interop.HTLEFT:
                    case Interop.HTRIGHT:
                    case Interop.HTTOP:
                    case Interop.HTTOPLEFT:
                    case Interop.HTTOPRIGHT:
                    case Interop.HTBOTTOM:
                    case Interop.HTBOTTOMLEFT:
                    case Interop.HTBOTTOMRIGHT:
                        if (autoResizeIsActive)
                            break;

                        if (_appSettings.Connection.Height.HasValue)
                            BeginInvoke(() => Height = _appSettings.Connection.Height.Value + 39);
                        if (_appSettings.Connection.Width.HasValue)
                            BeginInvoke(() => Width = _appSettings.Connection.Width.Value + 16);
                        return;
                }
                break;
        }

        base.WndProc(ref m);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BorderlessMinMaxInfo
    {
        public Point Reserved;
        public Size MaxSize;
        public Point MaxPosition;
        public Size MinTrackSize;
        public Size MaxTrackSize;
    }

    private bool IsBorderlessWindowed
        => FormBorderStyle == FormBorderStyle.None &&
           !(_rdpService?.FullScreen ?? false) &&
           WindowState != FormWindowState.Maximized;

    internal int GetBorderlessWindowHitTest(Point? screenPoint = null)
    {
        if (!_remoteChromeActive || !IsBorderlessWindowed)
            return Interop.HTCLIENT;
        var bounds = RectangleToScreen(ClientRectangle);
        if (!bounds.Contains(screenPoint ?? Cursor.Position))
            return Interop.HTCLIENT;
        var grip = Math.Max(6, (int)Math.Round(DeviceDpi / 12F));
        var cursor = screenPoint ?? Cursor.Position;
        var onLeft = cursor.X >= bounds.Left && cursor.X < bounds.Left + grip;
        var onRight = cursor.X < bounds.Right && cursor.X >= bounds.Right - grip;
        var onTop = cursor.Y >= bounds.Top && cursor.Y < bounds.Top + grip;
        var onBottom = cursor.Y < bounds.Bottom && cursor.Y >= bounds.Bottom - grip;

        if (IsTopRightControlArea(cursor) || IsToolbarArea(cursor))
            return Interop.HTCLIENT;

        if (onTop && onLeft)
            return Interop.HTTOPLEFT;
        if (onTop && onRight)
            return Interop.HTTOPRIGHT;
        if (onBottom && onLeft)
            return Interop.HTBOTTOMLEFT;
        if (onBottom && onRight)
            return Interop.HTBOTTOMRIGHT;
        if (onLeft)
            return Interop.HTLEFT;
        if (onRight)
            return Interop.HTRIGHT;
        if (onTop)
        {
            return Interop.HTTOP;
        }
        if (onBottom)
            return Interop.HTBOTTOM;

        return 1; // HTCLIENT
    }
    private static Point ScreenPointFromMessage(Message message)
    {
        var packed = message.LParam.ToInt64();
        return new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff));
    }
    /// <summary>
    /// Clean up any resources being used.
    /// </summary>
    /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_resourcesDisposed)
        {
            _resourcesDisposed = true;
            _globalSettingsWindow?.Dispose();
            _sessionClosing = true;
            StopAutomaticReconnect();
            _remoteChromeActive = false;
            // CleanupConnection raises events; detach before destroying any
            // owned toolbar windows so cleanup cannot try to show them again.
            UnwireRdpEvents();
            _rdpService.Dispose();
            if (_availabilityScanner != null)
            {
                _availabilityScanner.StatusChanged -= AvailabilityScanner_StatusChanged;
                _availabilityScanner.Dispose();
                _availabilityScanner = null;
            }
            foreach (var preview in _previewSessions.Values.ToArray())
                preview.Dispose();
            _previewSessions.Clear();
            foreach (var grip in _cornerGrips) grip.Dispose();
            _glassChromeStrip?.Dispose();
            _globalShortcutFilter?.Dispose();
            _resizeInputFilter?.Dispose();
            _resizeGestureTimer?.Dispose();
            _windowMoveTimer?.Dispose();
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            _trayMenu?.Dispose();
            _connectionLoader.Dispose();
            _formResizeDetectionService.Dispose();
            _formSizeEnforcementService.Dispose();
            _formSystemMenuService.Dispose();

            components?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void RdpService_Connected(object? sender, EventArgs e)
    {
        HideConnectingMessage();
        if (_automaticReconnectActive && _automaticReconnectAttemptInProgress)
            _reconnectDialog?.ShowConnecting(_automaticReconnectAttempts);
        panelStartup.Visible = false;
        panelRdp.Visible = true;
        panelRdp.BringToFront();
        if (!_previewOnly)
            SetRemoteConnectionChrome(true);
        if (_applyConnectionWindowSizeAfterConnect)
        {
            ApplyConnectionWindowSize();
            _applyConnectionWindowSizeAfterConnect = false;
        }
        // The connected callback can run before the maximize/layout pass has
        // completed. Queue one final update against the settled client area so
        // the remote session cannot fall back to ActiveX scrollbars.
        if (IsHandleCreated && !IsDisposed)
        {
            try
            {
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (!IsDisposed && !_sessionClosing && _rdpService.IsConnected)
                            _rdpService.ResizeSessionToContainer();
                    }
                    catch (COMException) { }
                    catch (InvalidOperationException) { }
                }));
            }
            catch (InvalidOperationException) { }
        }
        UpdateBadgeIcon();
        if (_isSessionWindow && !_previewOnly)
            Activate();
        if (!_previewOnly)
            _rdpService.FocusRemoteClient();
        RefreshGlobalShortcutHookOrder();
        _formSystemMenuService.EnableMenuItem(Interop.SC_FULLSCREEN, true);
        _managementWindow?._availabilityScanner?.MarkOnline(_appSettings.Connection.Hostname);
        _managementWindow?.ArrangeSessions();
    }

    private void RdpService_LoginCompleted(object? sender, EventArgs e)
    {
        if (_previewOnly)
            return;

        _reconnectFullScreen = _rdpService.FullScreen;
        _reconnectFullScreenMultiMonitor = _fullScreenMultiMonitor;
        StopAutomaticReconnect();

        RecentConnectionsService.AddOrUpdate(_appSettings.Connection, markConnected: true);
        _managementWindow?._connectionLibraryPanel?.Reload();
    }

    private void RdpService_Disconnected(object? sender, DisconnectedEventArgs e)
    {
        if (e.IsReconnect)
            return;

        // The next connection may be created with the same keyboard mode, but
        // the native RDP client is a new object and still needs the mode to be
        // applied once.  Keeping this marker would also skip the corresponding
        // shortcut-hook refresh on reconnect.
        _appliedKeyboardMode = null;

        if (_isSessionWindow && e.IsRemoteShutdown)
        {
            // A reported shutdown is terminal, unlike a reboot or transport
            // failure. Cancel any pending countdown before queuing Close so
            // no retry can run while the native disconnect callback unwinds.
            StopAutomaticReconnect();
            if (!_sessionClosing && !IsDisposed)
            {
                _sessionClosing = true;
                HideConnectingMessage();
                if (IsHandleCreated)
                    BeginInvoke(new Action(() =>
                    {
                        if (!IsDisposed) Close();
                    }));
                else
                    Close();
            }
            return;
        }

        if (_isSessionWindow && !_previewOnly && !_sessionClosing && !e.IsIntentionalDisconnect)
        {
            _reconnectFullScreen = e.WasFullScreen;
            _reconnectFullScreenMultiMonitor = e.WasMultiMonitor;
            if (_automaticReconnectActive)
            {
                if (_automaticReconnectAttemptInProgress)
                {
                    _automaticReconnectAttemptInProgress = false;
                    ScheduleAutomaticReconnect(e.ErrorDescription);
                }
            }
            else
            {
                BeginAutomaticReconnect(e.ErrorDescription);
            }

            return;
        }

        if (_isSessionWindow)
        {
            StopAutomaticReconnect();
            if (_previewOnly)
            {
                if (!IsDisposed && IsHandleCreated)
                    BeginInvoke(Close);
                return;
            }
            if (!_sessionClosing)
            {
                _managementWindow?.ShowDisconnectStatus(e);
                _managementWindow?._connectionLibraryPanel?.Reload();
                if (!IsDisposed && IsHandleCreated)
                    BeginInvoke(Close);
            }

            return;
        }

        panelRdp.Visible = false;
        panelStartup.Visible = true;
        panelStartup.BringToFront();
        SetRemoteConnectionChrome(false);
        _connectionLibraryPanel?.Reload();
        _formSystemMenuService.EnableMenuItem(Interop.SC_FULLSCREEN, false);

        HideConnectingMessage();
        ShowDisconnectStatus(e);

        var originalIcon = Properties.Resources.AppIcon;
        if (Icon != originalIcon)
        {
            Icon?.Dispose();
            Icon = originalIcon;
        }

        var closeOnDisconnect = !_launchedFromLibrary && _appSettings.NoCloseOnDisconnect != true;
        var closeNotSuppressed = !ModifierKeys.HasFlag(Keys.Shift);
        if (closeOnDisconnect && closeNotSuppressed && e.IsIntentionalDisconnect)
        {
            Close();
            Application.Exit();
        }
    }

    private void RdpService_FullScreenChanged(object? sender, FullScreenEventArgs e)
    {
        if (_isSessionWindow && !_previewOnly)
        {
            if (e.IsFullScreen)
            {
                _reconnectFullScreen = true;
                _reconnectFullScreenMultiMonitor = e.IsMultiMonitor;
            }
            else if (_rdpService.IsConnected)
            {
                _reconnectFullScreen = false;
                _reconnectFullScreenMultiMonitor = false;
            }
        }

        SetFullScreenMode(e.IsFullScreen, e.IsMultiMonitor);
    }

    private void RdpService_LeaveFullScreenRequested(object? sender, EventArgs e)
    {
        if (!_isSessionWindow || _sessionClosing || IsDisposed || !IsHandleCreated)
            return;

        // The ActiveX control can raise this callback while Windows is moving
        // focus from one RDP top-level window to another.  Treat only a request
        // that is still directed at this foreground session as an intentional
        // fullscreen change; otherwise switching windows must preserve this
        // session's fullscreen state.
        try
        {
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed || _sessionClosing || !_rdpService.FullScreen)
                    return;
                var management = _managementWindow ?? this;
                if (ReferenceEquals(management.GetForegroundSession(), this) ||
                    Form.ActiveForm == this || ContainsFocus)
                    _rdpService.FullScreen = false;
            }));
        }
        catch (InvalidOperationException) { }
    }

    private void RdpService_Minimized(object? sender, EventArgs e)
    {
        var management = _managementWindow ?? this;
        // Native RDP can report its container transition while PureRDP is
        // restoring a minimized batch. Do not let that late notification
        // re-minimize a window that has just been restored.
        if (_sessionClosing || IsDisposed || _restoringControlBarState ||
            management._bulkWindowStateTransition)
            return;
        WindowState = FormWindowState.Minimized;
    }

    private void ResizeDetected(object? sender, EventArgs e)
    {
        if (_restoringControlBarState || _fullScreenTransitionInProgress)
            return;
        QueueRemoteResize();
    }

    private void QueueRemoteResize()
    {
        var management = _managementWindow ?? this;
        if (!_isSessionWindow || !_remoteChromeActive || _restoringControlBarState ||
            _fullScreenTransitionInProgress ||
            management._bulkWindowStateTransition ||
            _suppressRemoteResizeAfterRestore ||
            WindowState == FormWindowState.Minimized ||
            IsDisposed || !IsHandleCreated || _remoteResizeQueued)
            return;

        _remoteResizeQueued = true;
        try
        {
            BeginInvoke(new Action(() =>
            {
                _remoteResizeQueued = false;
                try
                {
                    if (management._bulkWindowStateTransition || _fullScreenTransitionInProgress)
                        return;
                    if (_suppressRemoteResizeAfterRestore)
                        return;
                    if (!IsDisposed && !_sessionClosing && _rdpService.IsConnected &&
                        WindowState != FormWindowState.Minimized)
                        _rdpService.ResizeSessionToContainer();
                }
                catch (COMException) { }
                catch (InvalidOperationException) { }
            }));
        }
        catch (InvalidOperationException)
        {
            _remoteResizeQueued = false;
        }
    }

    private void FormRdpClient_Shown(object sender, EventArgs e)
    {
        if (_adoptedPreviewSession)
        {
            LoadAndApplyWindowSettings();
            if (_isSessionWindow && WindowState == FormWindowState.Normal && _appSettings.Window.ManualWindowX == null)
                Location = Screen.FromControl(_managementWindow ?? this).WorkingArea.Location;
            UpdateWindowTitle();
            SetRemoteConnectionChrome(true);

            if (_rdpService.IsConnected)
            {
                if (_restoreFullScreenOnConnect)
                {
                    if (!_rdpService.FullScreen)
                        _rdpService.FullScreen = true;
                    else
                        SetFullScreenMode(true, _appSettings.Connection.UseAllMonitors == true);
                    _restoreFullScreenOnConnect = false;
                }
                QueueRemoteResize();
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || _sessionClosing) return;
                    _rdpService.FocusRemoteClient();
                    RefreshGlobalShortcutHookOrder();
                }));
            }
            else if (!_rdpService.HasActiveControl)
            {
                StartConnection();
            }
            return;
        }

        if (_previewOnly)
        {
            PreparePreviewWindow();
            if (IsHandleCreated)
                BeginInvoke(new Action(() =>
                {
                    if (!IsDisposed && !_sessionClosing)
                        StartConnection();
                }));
            else
                StartConnection();
            return;
        }
        LoadAndApplyWindowSettings();
        if (_isSessionWindow && WindowState == FormWindowState.Normal && _appSettings.Window.ManualWindowX == null)
            Location = Screen.FromControl(_managementWindow ?? this).WorkingArea.Location;
        UpdateWindowTitle();
        if (_isSessionWindow)
        {
            // WindowState.Maximized is applied immediately, but Windows may
            // finish recalculating the working-area client size after Shown.
            // Start on the next UI turn so the first RDP dimensions come from
            // the settled client area rather than an old window size.
            if (IsHandleCreated)
                BeginInvoke(new Action(() =>
                {
                    if (!IsDisposed && !_sessionClosing)
                        StartConnection();
                }));
            else
                StartConnection();
        }
        else
        {
            // A silent instance's first Shown happens only after the user
            // explicitly opens the console. Never hide it again here.
            // The first explicit open can carry new .rdp arguments, replacing
            // SilentStartup in _appSettings before Shown is dispatched. That
            // command already owns AutoStartup; do not run it a second time.
            if (!_startedSilently && _appSettings.SilentStartup != true)
                AutoStartup();
        }
    }

    private void FormRdpClient_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_isSessionWindow && !_allowApplicationExit &&
            e.CloseReason is not CloseReason.WindowsShutDown and not CloseReason.TaskManagerClosing)
        {
            // The management page is a tray resident console. Closing its
            // window must not interrupt an active remote session.
            SaveWindowSettings();
            foreach (var session in _sessionWindows.ToArray())
                if (!session.IsDisposed) session.SaveWindowSettings();
            e.Cancel = true;
            Hide();
            return;
        }

        if (!_isSessionWindow)
            foreach (var session in _sessionWindows.ToArray())
                if (!session.IsDisposed) session.Close();

        _sessionClosing = true;
        StopAutomaticReconnect();
        if (_isSessionWindow)
        {
            // Release the native RDP session before the form handle is torn
            // down. Relying only on WinForms' later Dispose leaves the
            // ActiveX/COM buffers attached to the long-lived console for a
            // noticeable period after the window has disappeared.
            UnwireRdpEvents();
            _rdpService.Dispose();
        }
        SaveWindowSettings();
    }

    private void btnConnect_Click(object? sender, EventArgs e)
        => Connect();

    private void btnSettings_Click(object? sender, EventArgs e)
    {
        if (ShowSettings() && _settingsConnectRequested)
            Connect();
    }

    private void btnCancel_Click(object sender, EventArgs e)
        => Disconnect();

    private void FormRdpClient_FormClosed(object sender, FormClosedEventArgs e)
    {
        if (_isSessionWindow)
        {
            var management = _managementWindow;
            if (management != null)
            {
                management._sessionWindows.Remove(this);
                foreach (var key in management._shortcutReturns.Where(pair => ReferenceEquals(pair.Value.Window, this)).Select(pair => pair.Key).ToArray())
                    management._shortcutReturns.Remove(key);
                if (ReferenceEquals(management._allSessionMinimizePreferred, this))
                    management._allSessionMinimizePreferred = null;
                management._allSessionMinimizeSnapshots.Remove(this);
                if (management._sessionWindow == this)
                    management._sessionWindow = management._sessionWindows.LastOrDefault(s =>
                        !s.IsDisposed && !s._sessionClosing && !s._previewOnly);
                if (management._allSessionMinimizeSnapshots.Count == 0)
                {
                    management._allSessionsMinimized = false;
                    management._allSessionMinimizePreferred = null;
                }
                management.ArrangeSessions();
                management.QueueSessionCloseRecovery();
                if (management._sessionWindows.Count == 0 && management._previewSessions.Count == 0)
                    management.QueueIdleMemoryCleanup();
            }
            return;
        }

        Application.Exit();
    }

    private void QueueIdleMemoryCleanup()
    {
        if (_idleMemoryCleanupPending || IsDisposed || _sessionClosing ||
            _sessionWindows.Any(session => !session.IsDisposed && !session._sessionClosing && !session._previewOnly) ||
            _previewSessions.Count != 0)
            return;

        _idleMemoryCleanupPending = true;
        try
        {
            BeginInvoke(new Action(() =>
            {
                _idleMemoryCleanupPending = false;
                if (IsDisposed || _sessionClosing ||
                    _sessionWindows.Any(session => !session.IsDisposed && !session._sessionClosing && !session._previewOnly) ||
                    _previewSessions.Count != 0)
                    return;

                // Native allocations are not included in GetTotalMemory.
                // Record both measures; do not infer native cleanup from a
                // managed heap threshold or force collection on the UI thread.
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                ConnectionLogService.WriteDiagnostic($"idle-memory pid={process.Id}; managed-bytes={GC.GetTotalMemory(false)}; private-bytes={process.PrivateMemorySize64}; working-set-bytes={process.WorkingSet64}; handles={process.HandleCount}; sessions={_sessionWindows.Count}; previews={_previewSessions.Count}");
            }));
        }
        catch (InvalidOperationException)
        {
            _idleMemoryCleanupPending = false;
        }
    }
}
