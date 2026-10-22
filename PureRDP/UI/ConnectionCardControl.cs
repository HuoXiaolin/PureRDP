using PureRDP.Configuration;
using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using PureRDP.Services;

namespace PureRDP.UI;

/// <summary>
/// A responsive connection tile used by the connection library.
/// Text is rendered with real child controls so it remains reliable on every
/// Windows scaling configuration and in all three view modes.
/// </summary>
internal sealed class ConnectionCardControl : UserControl
{
    private string _titleText;
    private string _hostText;
    private string _metaText;
    private string _initial;
    private readonly CircularPanel _avatarPanel;
    private readonly FavoriteStarButton _pinButton;
    private readonly Label _avatarLabel;
    private readonly Label _titleLabel;
    private readonly Label _hostLabel;
    private readonly Label _metaLabel;
    private readonly Label _hintLabel;
    private readonly Label _qualityLabel;
    private readonly Panel _actionPanel;
    private readonly ModernButton _connectButton;
    private readonly ModernButton _editButton;
    private readonly ModernButton _deleteButton;
    private readonly PreviewSwitchControl _previewButton;
    private readonly RemotePreviewControl _preview;
    private readonly Label _previewTitle;
    private bool _previewEnabled;
    private DeviceAvailabilityState _availabilityState = DeviceAvailabilityState.Unknown;
    private ConnectionQualitySnapshot? _connectionQuality;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool PreviewEnabled
    {
        get => _previewEnabled;
        set
        {
            if (_previewEnabled == value) return;
            _previewEnabled = value;
            if (!value && _previewButton.Checked)
                _previewButton.Checked = false;
            UpdatePreviewLayout();
        }
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<Form?>? PreviewSource { get => _preview.SourceProvider; set => _preview.SourceProvider = value; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<PreviewSessionHost?>? PreviewHost { get => _preview.PreviewHostProvider; set => _preview.PreviewHostProvider = value; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool PreviewOpen { get => _previewButton.Checked; set => _previewButton.Checked = value; }
    private Color _accentColor;
    private Point _dragStart;
    private bool _dragStarted;
    private bool _wasDragged;
    private bool _hovered;
    private bool _dpiChanging;

    public ConnectionCardControl(ConnectionSettings settings)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _accentColor = ResolveAccentColor(settings);
        _titleText = settings.DisplayName ?? settings.Hostname ?? "未命名设备";
        _hostText = settings.Hostname ?? string.Empty;
        _metaText = GetUserText(settings);
        _initial = GetInitial(_titleText);

        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = UiDesignTokens.Surface;
        BorderStyle = BorderStyle.None;
        Cursor = Cursors.Hand;
        Margin = new Padding(UiDesignTokens.CardGap);
        MinimumSize = new Size(210, 56);
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = $"连接到 {_titleText}，{_hostText}";
        AccessibleDescription = "单击连接，按住拖动调整顺序，或使用卡片底部操作按钮。";

        _avatarLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = DeviceAccentColors.GetContrastingText(_accentColor),
            // One point smaller keeps initials inside the modern avatar rim
            // without changing the card's layout or interaction regions.
            Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
            Text = _initial,
            TextAlign = ContentAlignment.MiddleCenter,
            UseCompatibleTextRendering = true,
            Padding = Padding.Empty,
            Enabled = true,
        };
        _avatarPanel = new CircularPanel
        {
            BackColor = _accentColor,
            TabStop = false,
        };
        _avatarPanel.Controls.Add(_avatarLabel);

        _titleLabel = CreateTextLabel(_titleText, new Font(UiDesignTokens.FontFamily, 10.5F, FontStyle.Bold), UiDesignTokens.TextPrimary);
        _titleLabel.AutoEllipsis = true;
        _titleLabel.TextAlign = ContentAlignment.MiddleLeft;
        _titleLabel.UseCompatibleTextRendering = true;
        _hostLabel = CreateTextLabel(_hostText, new Font("Segoe UI", 9F), UiDesignTokens.TextSecondary);
        _metaLabel = CreateTextLabel(_metaText, new Font("Segoe UI", 8.5F), UiDesignTokens.TextMuted);
        _hintLabel = CreateTextLabel(FormatLastConnected(settings.LastConnected), new Font("Segoe UI", 8F), UiDesignTokens.TextMuted);
        _qualityLabel = CreateTextLabel(string.Empty, new Font(UiDesignTokens.FontFamily, 7.5F), UiDesignTokens.TextMuted);
        _qualityLabel.TextAlign = ContentAlignment.TopLeft;
        _qualityLabel.AutoEllipsis = false;
        _qualityLabel.UseCompatibleTextRendering = true;
        _qualityLabel.Visible = false;

        Controls.Add(_avatarPanel);
        Controls.Add(_titleLabel);
        Controls.Add(_hostLabel);
        Controls.Add(_metaLabel);
        Controls.Add(_hintLabel);
        Controls.Add(_qualityLabel);
        _pinButton = new FavoriteStarButton { TabIndex = 0 };
        _pinButton.Click += (_, _) => PinToggled?.Invoke(this);
        Controls.Add(_pinButton);

        _connectButton = CreateActionButton("连接", ModernButtonKind.Primary, "连接到此设备");
        _editButton = CreateActionButton("编辑", ModernButtonKind.Secondary, "编辑设备设置");
        _deleteButton = CreateActionButton("删除", ModernButtonKind.Ghost, "从设备列表删除");
        _previewButton = new PreviewSwitchControl
        {
            AccessibleName = "预览开关",
            AccessibleDescription = "打开或关闭此设备的预览窗",
            TabStop = true,
        };
        _preview = new RemotePreviewControl();
        _previewTitle = CreateTextLabel("预览窗", new Font("Microsoft YaHei UI", 8F), UiDesignTokens.TextMuted);
        Controls.Add(_preview);
        Controls.Add(_previewTitle);
        _previewButton.CheckedChanged += (_, _) =>
        {
            // Preview is content state: preserve the size assigned by the library.
            var showPreview = ViewMode == ConnectionLibraryViewMode.Cards && PreviewEnabled && PreviewOpen;
            _preview.Visible = showPreview;
            _previewTitle.Visible = false;
            LayoutActionPanel();
            Invalidate();
            PreviewToggled?.Invoke(this, _previewButton.Checked);
            if (_previewButton.Checked)
                _preview.RefreshPreview();
        };
        _actionPanel = new Panel { BackColor = UiDesignTokens.Surface };
        _actionPanel.Controls.Add(_connectButton);
        _actionPanel.Controls.Add(_editButton);
        _actionPanel.Controls.Add(_deleteButton);
        _actionPanel.Controls.Add(_previewButton);
        Controls.Add(_actionPanel);

        _connectButton.Click += (_, _) => ConnectClicked?.Invoke(this);
        _editButton.Click += (_, _) => EditClicked?.Invoke(this);
        _deleteButton.Click += (_, _) => DeleteClicked?.Invoke(this);

        AttachSurfaceMouseHandlers(this);
        AttachSurfaceMouseHandlers(_avatarPanel);
        AttachSurfaceMouseHandlers(_titleLabel);
        AttachSurfaceMouseHandlers(_hostLabel);
        AttachSurfaceMouseHandlers(_metaLabel);
        AttachSurfaceMouseHandlers(_hintLabel);
        SetViewMode(ConnectionLibraryViewMode.Cards);
    }

    public ConnectionSettings Settings { get; private set; }

    internal void UpdateSettings(ConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings.Clone();
        _titleText = Settings.DisplayName ?? Settings.Hostname ?? "未命名设备";
        _hostText = Settings.Hostname ?? string.Empty;
        _metaText = GetUserText(Settings);
        _initial = GetInitial(_titleText);
        _accentColor = ResolveAccentColor(Settings);

        _titleLabel.Text = _titleText;
        _hostLabel.Text = _hostText;
        _metaLabel.Text = _metaText;
        _hintLabel.Text = FormatLastConnected(Settings.LastConnected);
        _avatarLabel.Text = _initial;
        _avatarLabel.ForeColor = DeviceAccentColors.GetContrastingText(_accentColor);
        _avatarPanel.BackColor = _accentColor;
        AccessibleName = $"连接到 {_titleText}，{_hostText}";
        Invalidate();
    }

    internal void SetAvailability(DeviceAvailabilityState state)
    {
        if (_availabilityState == state) return;
        _availabilityState = state;
        Invalidate(new Rectangle(Math.Max(0, Width - 34), 2, Math.Min(34, Width), 30));
    }

    internal void SetConnectionQuality(ConnectionQualitySnapshot? snapshot)
    {
        if (Equals(_connectionQuality, snapshot))
            return;

        _connectionQuality = snapshot;
        _qualityLabel.Text = snapshot?.ToDisplayText() ?? string.Empty;
        _qualityLabel.ForeColor = snapshot?.Grade switch
        {
            ConnectionQualityGrade.Excellent or ConnectionQualityGrade.Good => UiDesignTokens.Accent,
            ConnectionQualityGrade.Fair => UiDesignTokens.Warning,
            ConnectionQualityGrade.Poor => UiDesignTokens.Danger,
            _ => UiDesignTokens.TextMuted,
        };
        _qualityLabel.Visible = snapshot?.IsActive == true;
        _qualityLabel.AccessibleName = snapshot?.ToAccessibleText() ?? string.Empty;
        LayoutActionPanel();
        Invalidate();
    }

    public ConnectionLibraryViewMode ViewMode { get; private set; }

    public event Action<ConnectionCardControl>? ConnectClicked;
    public event Action<ConnectionCardControl, bool>? PreviewToggled;
    public event Action<ConnectionCardControl>? EditClicked;
    public event Action<ConnectionCardControl>? DeleteClicked;
    public event Action<ConnectionCardControl>? DragRequested;
    public event Action<ConnectionCardControl>? PinToggled;
    internal bool IsPinned => _pinButton.IsPinned;
    internal void SetPinned(bool pinned) => _pinButton.IsPinned = pinned;

    public void SetViewMode(ConnectionLibraryViewMode mode)
    {
        ViewMode = mode;
        var isList = mode == ConnectionLibraryViewMode.List;
        var isHorizontal = mode == ConnectionLibraryViewMode.Horizontal;

        if (isList)
        {
            var parentWidth = Parent?.ClientSize.Width ?? 720;
            Width = Math.Max(Px(UiDesignTokens.ListCardMinimumWidth), parentWidth - Px(48));
        }
        else
        {
            Width = Px(isHorizontal ? 540 : 308);
        }

        var showPreview = !isList && !isHorizontal && PreviewEnabled && PreviewOpen;
        Height = Px(isList ? 96 : isHorizontal ? 108 : 190);
        _preview.Visible = showPreview;
        _previewTitle.Visible = false;
        _previewButton.Visible = !isList && !isHorizontal && PreviewEnabled;
        var actionWidth = isList ? 196 : isHorizontal ? 196 : 0;
        var actionHeight = 34;
        _actionPanel.SetBounds(0, 0, Math.Max(1, actionWidth), actionHeight);
        _hintLabel.Visible = true;
        LayoutActionPanel();
        Invalidate();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_pinButton.Focused && keyData is Keys.Enter or Keys.Space)
        {
            _pinButton.PerformClick();
            return true;
        }
        if (_previewButton.Focused && keyData is Keys.Enter or Keys.Space)
        {
            _previewButton.PerformClick();
            return true;
        }
        if (keyData is Keys.Enter or Keys.Space)
        {
            ConnectClicked?.Invoke(this);
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_actionPanel != null && !_dpiChanging) LayoutActionPanel();
    }

    protected override void OnDpiChangedBeforeParent(EventArgs e)
    {
        _dpiChanging = true;
        base.OnDpiChangedBeforeParent(e);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        _dpiChanging = false;
        RefreshDpiLayout();
    }

    internal void RefreshDpiLayout()
    {
        if (_actionPanel == null || IsDisposed || _dpiChanging) return;
        SuspendLayout();
        try
        {
            MinimumSize = new Size(Px(210), Px(56));
            Margin = new Padding(Px(UiDesignTokens.CardGap));
            Height = Px(ViewMode == ConnectionLibraryViewMode.List ? 96 :
                ViewMode == ConnectionLibraryViewMode.Horizontal ? 108 : 190);
            LayoutActionPanel();
        }
        finally { ResumeLayout(false); }
        Invalidate(true);
    }

    private int Px(int logical) => (int)Math.Round(logical * DeviceDpi / 96.0);
    private int Logical(int physical) => (int)Math.Round(physical * 96.0 / DeviceDpi);
    private void SetLogicalBounds(Control control, int x, int y, int width, int height)
        => control.SetBounds(Px(x), Px(y), Math.Max(1, Px(width)), Math.Max(1, Px(height)));

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
        e.Graphics.Clear(UiDesignTokens.PageBackground);
        var cardBounds = new Rectangle(2, 2, Math.Max(1, Width - 5), Math.Max(1, Height - 5));
        using var cardPath = RoundedPath(cardBounds, Math.Min(UiDesignTokens.SurfaceRadius, Math.Min(cardBounds.Width, cardBounds.Height) / 3));

        // A restrained shadow gives the card depth while keeping the green-and-sky
        // palette light and uncluttered.
        using (var shadowPath = RoundedPath(new Rectangle(cardBounds.X, cardBounds.Y + 2,
                   cardBounds.Width, cardBounds.Height), Math.Min(UiDesignTokens.SurfaceRadius, Math.Min(cardBounds.Width, cardBounds.Height) / 3)))
        using (var shadowBrush = new SolidBrush(Color.FromArgb(_hovered ? 24 : 14, 34, 92, 64)))
            e.Graphics.FillPath(shadowBrush, shadowPath);

        using var fill = new SolidBrush(UiDesignTokens.Surface);
        e.Graphics.FillPath(fill, cardPath);
        var borderColor = Focused ? UiDesignTokens.Accent : _hovered ? UiDesignTokens.BorderStrong : UiDesignTokens.Border;
        using var borderPen = new Pen(borderColor, Focused ? 2F : 1F);
        e.Graphics.DrawPath(borderPen, cardPath);

        if (_hovered || Focused)
        {
            using var accentBrush = new SolidBrush(Color.FromArgb(Focused ? 220 : 150, UiDesignTokens.Accent));
            e.Graphics.FillRectangle(accentBrush, new Rectangle(2, 18, 4, Math.Max(18, Height - 36)));
        }

        var statusColor = _availabilityState switch
        {
            DeviceAvailabilityState.Online => Color.FromArgb(34, 169, 83),
            DeviceAvailabilityState.Offline => Color.FromArgb(204, 63, 57),
            _ => Color.FromArgb(173, 190, 182),
        };
        var dotBounds = new Rectangle(Math.Max(8, Width - 23), 11, 10, 10);
        using var dotBrush = new SolidBrush(statusColor);
        using var dotBorder = new Pen(Color.FromArgb(220, 255, 255, 255), 1F);
        e.Graphics.FillEllipse(dotBrush, dotBounds);
        e.Graphics.DrawEllipse(dotBorder, dotBounds);
    }

    private void LayoutTextAndAvatar()
    {
        if (_actionPanel == null || _dpiChanging)
            return;

        var width = Logical(Width);
        var isList = ViewMode == ConnectionLibraryViewMode.List && width >= 540;
        var isHorizontal = ViewMode == ConnectionLibraryViewMode.Horizontal && width >= 540;
        foreach (var label in new[] { _titleLabel, _hostLabel, _metaLabel, _hintLabel, _qualityLabel })
            ((CardTextLabel)label).SingleLine = ViewMode == ConnectionLibraryViewMode.List || ReferenceEquals(label, _titleLabel);
        var qualityLines = (_connectionQuality?.ToDisplayText() ?? string.Empty).Split('\n');
        if (ViewMode == ConnectionLibraryViewMode.List)
        {
            // Explorer-style columns: every field remains on the same baseline.
            _qualityLabel.Text = string.Join(" · ", qualityLines);
            _qualityLabel.AutoEllipsis = true;
            _qualityLabel.TextAlign = ContentAlignment.MiddleLeft;
            SetLogicalBounds(_pinButton, width - 64, 14, 28, 28);
            SetLogicalBounds(_avatarPanel, 16, 14, 28, 28);
            SetLogicalBounds(_titleLabel, 54, 14, 104, 28);
            SetLogicalBounds(_hostLabel, 166, 14, 160, 28);
            SetLogicalBounds(_metaLabel, 334, 14, 120, 28);
            SetLogicalBounds(_hintLabel, 462, 14, 150, 28);
            SetLogicalBounds(_qualityLabel, 620, 14, Math.Max(1, Logical(_actionPanel.Left) - 632), 28);
            return;
        }
        _qualityLabel.AutoEllipsis = false;
        _qualityLabel.TextAlign = ContentAlignment.TopLeft;
        _qualityLabel.Text = !isList && !isHorizontal && qualityLines.Length == 4
            ? $"{qualityLines[0]} · {qualityLines[1]}\n{qualityLines[2]} · {qualityLines[3]}"
            : _connectionQuality?.ToDisplayText() ?? string.Empty;
        SetLogicalBounds(_pinButton, width - 64,
            !isList && !isHorizontal ? 12 : 8, 28, 28);
        if (ViewMode == ConnectionLibraryViewMode.Cards && width >= 700)
        {
            var showPreview = PreviewEnabled && PreviewOpen;
            var bodyRight = showPreview ? width - 184 : width - 18;
            var columnWidth = (bodyRight - 42) / 3;
            SetLogicalBounds(_avatarPanel, 18, 16, 30, 30);
            SetLogicalBounds(_titleLabel, 58, 16, width - 132, 30);
            SetLogicalBounds(_hostLabel, 18, 50, columnWidth, 20);
            SetLogicalBounds(_metaLabel, 30 + columnWidth, 50, columnWidth, 20);
            SetLogicalBounds(_hintLabel, 42 + columnWidth * 2, 50, columnWidth, 20);
            SetLogicalBounds(_qualityLabel, 18, 78, bodyRight - 18, 32);
            if (showPreview)
                SetLogicalBounds(_preview, width - 168, 48, 150, 150 * 9 / 16);
            return;
        }
        if (ViewMode == ConnectionLibraryViewMode.Cards && PreviewEnabled && PreviewOpen)
        {
            // Keep identity across the full header; telemetry must never squeeze
            // a short device name into a stack of individual letters.
            var previewWidth = Math.Max(110, Math.Min(170, (width - 48) / 2));
            var previewLeft = width - 18 - previewWidth;
            var textWidthAvailable = Math.Max(40, previewLeft - 34);
            SetLogicalBounds(_avatarPanel, 18, 16, 30, 30);
            SetLogicalBounds(_titleLabel, 58, 16, Math.Max(40, width - 132), 30);
            SetLogicalBounds(_hostLabel, 18, 48, textWidthAvailable, 20);
            SetLogicalBounds(_metaLabel, 18, 70, textWidthAvailable, 18);
            SetLogicalBounds(_hintLabel, 18, 90, textWidthAvailable, 18);
            SetLogicalBounds(_qualityLabel, 18, 112, textWidthAvailable, 32);
            SetLogicalBounds(_previewTitle, previewLeft, 49, previewWidth, 18);
            SetLogicalBounds(_preview, previewLeft, 48, previewWidth, previewWidth * 9 / 16);
            return;
        }
        if (!isList && !isHorizontal)
        {
            SetLogicalBounds(_avatarPanel, 18, 16, 30, 30);
            SetLogicalBounds(_titleLabel, 58, 16, Math.Max(40, width - 132), 30);
            SetLogicalBounds(_qualityLabel, 18, 112, Math.Max(1, width - 36), 32);
            SetLogicalBounds(_hostLabel, 18, 48, Math.Max(1, width - 36), 20);
            SetLogicalBounds(_metaLabel, 18, 70, Math.Max(1, width - 36), 18);
            SetLogicalBounds(_hintLabel, 18, 90, Math.Max(1, width - 36), 18);
            return;
        }
        var avatarSize = isList ? 40 : 44;
        var avatarBounds = new Rectangle(18, isList ? 18 : 20, avatarSize, avatarSize);
        SetLogicalBounds(_avatarPanel, avatarBounds.X, avatarBounds.Y, avatarBounds.Width, avatarBounds.Height);

        var textX = isList ? 70 : 74;
        var textRight = isList || isHorizontal ? Logical(_actionPanel.Left) - 16 : width - 20;
        var qualityWidth = Math.Min(128, Math.Max(96, (textRight - textX) / 2));
        var qualityLeft = Math.Max(textX + 40, textRight - qualityWidth);
        var textWidth = Math.Max(40, qualityLeft - textX - 8);
        var top = isList ? 10 : 14;
        SetLogicalBounds(_titleLabel, textX, top, textWidth, 24);
        SetLogicalBounds(_hostLabel, textX, top + 26, textWidth, 20);
        SetLogicalBounds(_metaLabel, textX, top + 48, textWidth, 18);
        SetLogicalBounds(_hintLabel, textX, top + 68, textWidth, 18);
        SetLogicalBounds(_qualityLabel, qualityLeft, isList ? 10 : isHorizontal ? 14 : 16,
            Math.Max(1, textRight - qualityLeft), 70);
    }

    private void LayoutActionPanel()
    {
        if (_actionPanel == null || _dpiChanging) return;
        var width = Logical(Width);
        var isList = ViewMode == ConnectionLibraryViewMode.List;
        var isHorizontal = ViewMode == ConnectionLibraryViewMode.Horizontal && width >= 540;
        // Session state changes content, never tile geometry. Reserve the same
        // body height before/after preview and connection-quality updates.
        var height = isList ? 56 : isHorizontal ? 108 :
            ViewMode == ConnectionLibraryViewMode.Cards && width >= 700 ? 178 : 190;
        if (Height != Px(height)) Height = Px(height);
        var actionWidth = isList || isHorizontal ? 198 : Math.Max(1, width - 40);
        SetLogicalBounds(_actionPanel, isList ? width - actionWidth - 90 : isHorizontal ? width - actionWidth - 18 : 20,
            isList ? 11 : isHorizontal ? Math.Max(40, (height - 34) / 2) : height - 42, actionWidth, 34);
        var connectWidth = isList || isHorizontal ? 82 : Math.Max(80, actionWidth - 120);
        SetLogicalBounds(_connectButton, 0, 0, connectWidth, 34);
        SetLogicalBounds(_editButton, connectWidth + 8, 0, 52, 34);
        SetLogicalBounds(_deleteButton, connectWidth + 64, 0, 52, 34);
        if (!isList && !isHorizontal && PreviewEnabled)
        {
            var primaryWidth = Math.Max(50, (actionWidth - 128) / 2);
            SetLogicalBounds(_connectButton, 0, 0, primaryWidth, 34);
            SetLogicalBounds(_previewButton, primaryWidth + 8, 0, primaryWidth, 34);
            SetLogicalBounds(_editButton, actionWidth - 112, 0, 52, 34);
            SetLogicalBounds(_deleteButton, actionWidth - 52, 0, 52, 34);
        }
        LayoutTextAndAvatar();
    }

    private void UpdatePreviewLayout()
    {
        if (_preview == null || _previewTitle == null || _previewButton == null || _actionPanel == null)
            return;

        var showPreview = ViewMode == ConnectionLibraryViewMode.Cards && _previewEnabled && _previewButton.Checked;
        _preview.Visible = showPreview;
        _previewTitle.Visible = false;
        _previewButton.Visible = ViewMode == ConnectionLibraryViewMode.Cards && _previewEnabled;
        LayoutActionPanel();
        Invalidate();
    }

    private void AttachSurfaceMouseHandlers(Control control)
    {
        control.MouseDown += SurfaceMouseDown;
        control.MouseMove += SurfaceMouseMove;
        control.MouseUp += SurfaceMouseUp;
        control.MouseEnter += SurfaceMouseEnter;
        control.MouseLeave += SurfaceMouseLeave;
    }

    private void SurfaceMouseEnter(object? sender, EventArgs e)
    {
        _hovered = true;
        Invalidate();
    }

    private void SurfaceMouseLeave(object? sender, EventArgs e)
    {
        _hovered = false;
        Invalidate();
    }

    private Point ToCardPoint(object? sender, Point location)
    {
        if (sender is Control control && !ReferenceEquals(control, this))
            return PointToClient(control.PointToScreen(location));
        return location;
    }

    private void SurfaceMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;

        _dragStart = ToCardPoint(sender, e.Location);
        _dragStarted = false;
        _wasDragged = false;
        Focus();
    }

    private void SurfaceMouseMove(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _dragStarted)
            return;

        var point = ToCardPoint(sender, e.Location);
        var dragRectangle = new Rectangle(
            _dragStart.X - SystemInformation.DragSize.Width / 2,
            _dragStart.Y - SystemInformation.DragSize.Height / 2,
            SystemInformation.DragSize.Width,
            SystemInformation.DragSize.Height);

        if (!dragRectangle.Contains(point))
        {
            _dragStarted = true;
            _wasDragged = true;
            DragRequested?.Invoke(this);
        }
    }

    private void SurfaceMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;

        var point = ToCardPoint(sender, e.Location);
        var connect = !_dragStarted && !_wasDragged && !_actionPanel.Bounds.Contains(point);
        _dragStarted = false;
        _wasDragged = false;
        if (connect)
            ConnectClicked?.Invoke(this);
    }

    private static Label CreateTextLabel(string text, Font font, Color foreColor)
    {
        return new CardTextLabel
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = foreColor,
            Font = font,
            Text = text,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Enabled = true,
        };
    }

    private sealed class CardTextLabel : Label
    {
        [DefaultValue(false)]
        internal bool SingleLine { get; set; }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (!SingleLine) { base.OnPaint(e); return; }
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
    }

    private static ModernButton CreateActionButton(string text, ModernButtonKind kind, string accessibleName)
    {
        var button = new ModernButton
        {
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
            Text = text,
            Kind = kind,
            Cursor = Cursors.Hand,
            TabStop = true,
            AccessibleName = accessibleName,
            AccessibleRole = AccessibleRole.PushButton,
        };
        return button;
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

    private static Color ResolveAccentColor(ConnectionSettings settings)
        => DeviceAccentColors.Resolve(settings);

    private static string GetInitial(string? hostname)
        => DeviceAccentColors.GetInitial(hostname);

    private static string GetUserText(ConnectionSettings settings)
    {
        var user = settings.Username;
        if (!string.IsNullOrWhiteSpace(settings.Domain) && !string.IsNullOrWhiteSpace(user))
            user = $"{settings.Domain}\\{user}";

        return string.IsNullOrWhiteSpace(user) ? "未设置用户名" : user;
    }

    private static string FormatLastConnected(DateTime lastConnected)
    {
        if (lastConnected == default)
            return "尚未连接";

        var local = lastConnected.ToLocalTime();
        var elapsed = DateTime.Now - local;
        if (elapsed.TotalMinutes < 1)
            return "刚刚连接";
        if (elapsed.TotalHours < 1)
            return $"{Math.Max(1, (int)elapsed.TotalMinutes)} 分钟前连接";
        if (elapsed.TotalDays < 1)
            return $"{Math.Max(1, (int)elapsed.TotalHours)} 小时前连接";
        if (local.Date == DateTime.Today.AddDays(-1).Date)
            return $"昨天 {local:HH:mm} 连接";

        return $"上次连接：{local:yyyy-MM-dd HH:mm}";
    }
}

internal static class DeviceAccentColors
{
    internal static Color Resolve(ConnectionSettings settings)
    {
        if (settings.BadgeColor.HasValue)
        {
            var color = settings.BadgeColor.Value;
            return Color.FromArgb(255, color.R, color.G, color.B);
        }

        var value = settings.Hostname ?? "设备";
        var palette = new[]
        {
            Color.FromArgb(44, 126, 74),
            Color.FromArgb(49, 132, 173),
            Color.FromArgb(90, 137, 55),
            Color.FromArgb(55, 119, 140),
            Color.FromArgb(66, 126, 89),
            Color.FromArgb(13, 148, 136),
        };
        uint hash = 2166136261;
        foreach (var c in value) hash = unchecked((hash ^ c) * 16777619);
        return palette[hash % (uint)palette.Length];
    }

    internal static Color GetContrastingText(Color background)
    {
        var luminance = (background.R * 0.299 + background.G * 0.587 + background.B * 0.114) / 255.0;
        return luminance >= 0.58 ? Color.FromArgb(24, 57, 48) : Color.White;
    }

    internal static string GetInitial(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "R";

        return name.Trim()[0].ToString().ToUpperInvariant();
    }
}

/// <summary>
/// Compact state button used for per-device preview state.
/// </summary>
internal sealed class PreviewSwitchControl : Control
{
    private bool _checked;
    private bool _hovered;
    private string _labelText = "预览";

    [DefaultValue("预览")]
    public string LabelText
    {
        get => _labelText;
        set
        {
            _labelText = value ?? string.Empty;
            Invalidate();
        }
    }

    [DefaultValue(false)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? CheckedChanged;

    public PreviewSwitchControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(72, 34);
        Cursor = Cursors.Hand;
        BackColor = UiDesignTokens.Surface;
        ForeColor = UiDesignTokens.TextSecondary;
        Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold);
        AccessibleRole = AccessibleRole.CheckButton;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Checked = !Checked;
    }

    protected override bool IsInputKey(Keys keyData)
        => keyData is Keys.Space or Keys.Enter || base.IsInputKey(keyData);

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is Keys.Space or Keys.Enter)
        {
            PerformClick();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    internal void PerformClick() => OnClick(EventArgs.Empty);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        // The action panel is transparent. Always paint the card surface first
        // so the control cannot fall back to the black classic-WinForms
        // transparent background.
        e.Graphics.Clear(UiDesignTokens.Surface);

        // Render one compact button instead of a label and a distant slider.
        // Both states stay in the app's solid green palette and remain easy to
        // recognize even when the card is resized or DPI-scaled.
        var buttonBounds = new Rectangle(0, 2, Math.Max(1, Width - 1), Math.Max(1, Height - 5));
        using var buttonPath = RoundedPath(buttonBounds, Math.Min(10, Math.Max(2, buttonBounds.Height / 2)));
        var buttonColor = _checked
            ? (_hovered ? Color.FromArgb(36, 132, 78) : UiDesignTokens.Accent)
            : (_hovered ? Color.FromArgb(214, 238, 222) : UiDesignTokens.AccentSoft);
        using (var buttonBrush = new SolidBrush(buttonColor))
            e.Graphics.FillPath(buttonBrush, buttonPath);

        var text = _checked && LabelText == "预览" ? "预览中" : LabelText;
        var textColor = _checked ? Color.White : UiDesignTokens.Accent;
        TextRenderer.DrawText(e.Graphics, text, Font, buttonBounds, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
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

