using PureRDP.Configuration;
using PureRDP.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PureRDP.UI;

/// <summary>
/// Connection home screen with search, three layouts, drag ordering and quick actions.
/// </summary>
internal sealed class ConnectionLibraryPanel : UserControl
{
    private const string SearchPlaceholder = "搜索设备、地址或用户名";
    private readonly Label _countLabel;
    private readonly Label _statusLabel;
    private readonly Dictionary<string, DeviceAvailabilityState> _availabilityStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBox _searchBox;
    private readonly RoundedSurfacePanel _searchShell;
    private readonly ComboBox _viewModeComboBox;
    private readonly List<ModernButton> _viewButtons = [];
    private readonly FlowLayoutPanel _itemsPanel;
    private readonly Panel _emptyPanel;
    private readonly Label _emptyTitleLabel;
    private readonly Label _emptyHintLabel;
    private readonly List<ConnectionSettings> _connections = [];
    private readonly HashSet<string> _openPreviewKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _qualityRefreshTimer = new() { Interval = 1000 };
    private readonly ModernButton _refreshButton;
    private ConnectionLibraryViewMode _viewMode = ConnectionLibraryViewMode.Cards;
    private ConnectionCardControl? _draggedCard;
    private bool _suppressViewModeEvent;

    public ConnectionLibraryPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
        BackColor = UiDesignTokens.PageBackground;
        Dock = DockStyle.Fill;
        Padding = Padding.Empty;

        var header = new SkyHeroPanel
        {
            Dock = DockStyle.Top,
            Height = UiDesignTokens.HeaderHeight,
            BackColor = UiDesignTokens.HeroSky,
            Padding = new Padding(24, 0, 24, 0),
        };

        var titlePanel = new Panel
        {
            Dock = DockStyle.None,
            BackColor = Color.Transparent,
        };
        var titleLabel = new HeaderTextLabel
        {
            AutoSize = true,
            Font = new Font(UiDesignTokens.FontFamily, 18F, FontStyle.Bold),
            ForeColor = UiDesignTokens.TextPrimary,
            Text = "远程桌面中枢",
            Location = new Point(0, 38),
        };
        var brandLabel = new HeaderTextLabel
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
            ForeColor = UiDesignTokens.Accent,
            Text = "PureRDP",
            Location = new Point(0, 14),
        };
        var subtitleLabel = new HeaderTextLabel
        {
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 10.5F),
            ForeColor = UiDesignTokens.TextSecondary,
            Text = "设备、连接与实时状态",
            Location = new Point(0, 82),
        };
        _countLabel = new HeaderTextLabel
        {
            Name = "ConnectionCountLabel",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            ForeColor = UiDesignTokens.Accent,
            BackColor = Color.Transparent,
            Padding = Padding.Empty,
            Text = "0 台设备",
            Location = new Point(0, 112),
        };
        titlePanel.Controls.Add(_countLabel);
        titlePanel.Controls.Add(subtitleLabel);
        titlePanel.Controls.Add(titleLabel);
        titlePanel.Controls.Add(brandLabel);

        _searchBox = new TextBox
        {
            AccessibleName = "搜索设备",
            BorderStyle = BorderStyle.None,
            Font = new Font("Microsoft YaHei UI", 10F),
            ForeColor = UiDesignTokens.TextMuted,
            BackColor = UiDesignTokens.Surface,
            PlaceholderText = SearchPlaceholder,
            TabIndex = 0,
            AutoSize = false,
            Height = 32,
            Width = 250,
        };
        _searchShell = new RoundedSurfacePanel
        {
            Padding = new Padding(14, 3, 14, 3),
            Size = new Size(294, 40),
        };
        _searchShell.Controls.Add(_searchBox);
        _searchShell.Click += (_, _) => _searchBox.Focus();
        _viewModeComboBox = new ModernComboBox
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            AccessibleName = "选择设备列表视图",
            DropDownStyle = ComboBoxStyle.DropDownList,
            TabIndex = 1,
            Font = new Font("Microsoft YaHei UI", 9.5F),
            Height = UiDesignTokens.ControlHeight,
            Width = 112,
            BackColor = UiDesignTokens.Surface,
        };
        _viewModeComboBox.Items.Add(new ViewModeItem(ConnectionLibraryViewMode.Cards, "卡片视图"));
        _viewModeComboBox.Items.Add(new ViewModeItem(ConnectionLibraryViewMode.Horizontal, "横排视图"));
        _viewModeComboBox.Items.Add(new ViewModeItem(ConnectionLibraryViewMode.List, "列表视图"));
        _viewModeComboBox.SelectedIndexChanged += ViewModeComboBox_SelectedIndexChanged;
        _viewModeComboBox.Visible = false;
        var viewSwitcher = new FlowLayoutPanel
        {
            Size = new Size(282, 40), WrapContents = false,
            BackColor = UiDesignTokens.SurfaceSoft, Padding = new Padding(4, 4, 4, 4),
        };
        foreach (var caption in new[] { "卡片", "横排", "列表" })
        {
            var index = _viewButtons.Count;
            var button = new ModernButton { Text = caption, Width = 90, Height = 32,
                Margin = Padding.Empty, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                Font = new Font("Microsoft YaHei UI", 9F), Kind = ModernButtonKind.Secondary, Bordered = false };
            button.Click += (_, _) => _viewModeComboBox.SelectedIndex = index;
            _viewButtons.Add(button);
            viewSwitcher.Controls.Add(button);
        }

        var addButton = new ModernButton
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            AccessibleName = "新建设备",
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold),
            Text = "+ 新建设备",
            Height = UiDesignTokens.ControlHeight,
            Width = 120,
            Kind = ModernButtonKind.Primary,
            TabStop = true,
            TabIndex = 2,
        };
        addButton.Click += (_, _) => NewConnectionRequested?.Invoke();
        var globalButton = new LinkLabel
        {
            Text = "全局设置", AccessibleName = "全局设置",
            Font = new Font("Microsoft YaHei UI", 9F), Width = 104, Height = 36,
            BackColor = Color.Transparent, LinkColor = UiDesignTokens.TextSecondary,
            ActiveLinkColor = UiDesignTokens.Accent, LinkBehavior = LinkBehavior.HoverUnderline,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        globalButton.Click += (_, _) => GlobalSettingsRequested?.Invoke();

        _refreshButton = new ModernButton
        {
            Name = "RefreshStatusButton",
            Text = "刷新状态",
            AccessibleName = "刷新网络和设备状态",
            AccessibleDescription = "立即检查设备地址和端口，重新采样已连接和预览会话的网络状态",
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            Kind = ModernButtonKind.Secondary,
            TabIndex = 3,
        };
        _refreshButton.Click += RefreshButton_Click;

        var logoPicture = new PictureBox
        {
            Size = new Size(98, 98),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
            Image = Properties.Resources.AppIcon.ToBitmap(),
            TabStop = false,
        };

        void LayoutHeaderActions()
        {
            var right = header.ClientSize.Width - header.Padding.Right;
            var left = header.Padding.Left;
            // Stack using actual font heights so the headings stay clear at
            // different display scales, sharing the search field's left edge.
            brandLabel.Location = new Point(0, 14);
            titleLabel.Location = new Point(0, brandLabel.Bottom + 4);
            subtitleLabel.Location = new Point(0, titleLabel.Bottom + 2);
            _countLabel.Location = new Point(0, subtitleLabel.Bottom + 6);
            titlePanel.SetBounds(left, 0, Math.Max(240, right - 264), _countLabel.Bottom + 8);
            logoPicture.SetBounds(Math.Max(24, right - 182), 24, 48, 48);
            logoPicture.Visible = header.Width >= 820;
            globalButton.SetBounds(Math.Max(24, right - 104), 24, 104, 36);
            _refreshButton.SetBounds(Math.Max(24, right - 104), 70, 104, 36);
            var actionsY = Math.Max(130, _countLabel.Bottom + 12);
            var compact = header.ClientSize.Width < 820;
            var switchY = compact ? actionsY + 50 : actionsY;
            header.Height = switchY + 58;
            addButton.SetBounds(right - addButton.Width, switchY, addButton.Width, 40);
            var searchWidth = compact ? Math.Max(120, right - 24) : Math.Min(370, Math.Max(120, right - 460));
            _searchShell.SetBounds(left, actionsY, searchWidth, 40);
            _searchBox.SetBounds(14, Math.Max(4, (40 - _searchBox.PreferredHeight) / 2),
                Math.Max(80, searchWidth - 28), _searchBox.PreferredHeight);
            viewSwitcher.Location = new Point(Math.Max(24,
                right - addButton.Width - 10 - viewSwitcher.Width), switchY);
            _countLabel.Visible = header.Width > 520;
        }

        header.Resize += (_, _) => LayoutHeaderActions();
        header.Controls.Add(titlePanel);
        header.Controls.Add(_searchShell);
        header.Controls.Add(_viewModeComboBox);
        header.Controls.Add(addButton);
        header.Controls.Add(viewSwitcher);
        header.Controls.Add(logoPicture);
        header.Controls.Add(globalButton);
        header.Controls.Add(_refreshButton);
        viewSwitcher.BringToFront();
        _searchBox.TextChanged += (_, _) => RenderConnections();
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape && _searchBox.TextLength > 0)
            {
                _searchBox.Clear();
                e.SuppressKeyPress = true;
            }
        };
        _searchShell.BringToFront();
        _viewModeComboBox.BringToFront();
        addButton.BringToFront();
        Controls.Add(header);
        header.PerformLayout();
        LayoutHeaderActions();

        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 28,
            AutoEllipsis = true,
            BackColor = UiDesignTokens.DangerSoft,
            ForeColor = UiDesignTokens.Danger,
            Font = new Font("Segoe UI", 8.5F),
            Padding = new Padding(24, 5, 20, 0),
            Visible = false,
        };
        Controls.Add(_statusLabel);

        _itemsPanel = new BufferedFlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = UiDesignTokens.PageBackground,
            AccessibleName = "设备列表",
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(UiDesignTokens.PagePadding, UiDesignTokens.PagePadding, UiDesignTokens.PagePadding, 22),
            AllowDrop = true,
        };
        _itemsPanel.DragEnter += ItemsPanel_DragEnter;
        _itemsPanel.DragOver += ItemsPanel_DragOver;
        _itemsPanel.DragDrop += ItemsPanel_DragDrop;
        _itemsPanel.ClientSizeChanged += (_, _) => UpdateCardSizes();
        _qualityRefreshTimer.Tick += (_, _) => RefreshConnectionQuality();

        _emptyPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiDesignTokens.PageBackground,
            Visible = false,
        };
        _emptyTitleLabel = new Label
        {
            AutoSize = false,
            Font = new Font("Segoe UI", 16F, FontStyle.Bold),
            ForeColor = Color.FromArgb(31, 41, 55),
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "还没有保存的远程设备",
        };
        _emptyHintLabel = new Label
        {
            AutoSize = false,
            Font = new Font("Segoe UI", 10F),
            ForeColor = UiDesignTokens.TextMuted,
            TextAlign = ContentAlignment.TopCenter,
            Text = "点击右上角“新建设备”，或者连接一次设备后，它会自动出现在这里。",
        };
        _emptyPanel.Controls.Add(_emptyHintLabel);
        _emptyPanel.Controls.Add(_emptyTitleLabel);

        var contentPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiDesignTokens.PageBackground,
        };
        contentPanel.Controls.Add(_itemsPanel);
        contentPanel.Controls.Add(_emptyPanel);
        Controls.Add(contentPanel);

        // WinForms lays out docked controls from the back of the z-order.
        // Put the fill panel last in that layout pass so it starts below the
        // header instead of covering it and leaving only the card footer visible.
        Controls.SetChildIndex(contentPanel, 0);
        Controls.SetChildIndex(_statusLabel, 1);
        Controls.SetChildIndex(header, 2);
        _emptyPanel.BringToFront();

        Resize += (_, _) => CenterEmptyPanel();
        Load += (_, _) =>
        {
            Reload();
            UpdateQualityRefreshTimer();
        };
        VisibleChanged += (_, _) => UpdateQualityRefreshTimer();
    }

    public event Action<ConnectionSettings>? ConnectRequested;
    public event Action<ConnectionSettings, bool>? PreviewToggledRequested;
    public event Action<ConnectionSettings>? EditRequested;
    public event Action<ConnectionSettings>? DeleteRequested;
    public event Action? NewConnectionRequested;
    public event Action? GlobalSettingsRequested;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<ConnectionSettings, Form?>? PreviewSourceProvider { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<ConnectionSettings, PreviewSessionHost?>? PreviewHostProvider { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<ConnectionSettings, ConnectionQualitySnapshot?>? ConnectionQualityProvider { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<Task>? StatusRefreshRequested { get; set; }
    private bool _previewsEnabled = true;

    private async void RefreshButton_Click(object? sender, EventArgs e)
    {
        if (!_refreshButton.Enabled || StatusRefreshRequested == null) return;
        _refreshButton.Enabled = false;
        _refreshButton.Text = "刷新中…";
        try
        {
            await StatusRefreshRequested();
            RefreshConnectionQuality();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!IsDisposed && !Disposing)
                ShowStatus("状态刷新失败，请稍后重试。");
        }
        finally
        {
            if (!IsDisposed && !Disposing)
            {
                _refreshButton.Text = "刷新状态";
                _refreshButton.Enabled = true;
            }
        }
    }

    private void UpdateQualityRefreshTimer()
    {
        if (IsDisposed || !IsHandleCreated || !Visible || ConnectionQualityProvider == null)
            _qualityRefreshTimer.Stop();
        else if (!_qualityRefreshTimer.Enabled)
            _qualityRefreshTimer.Start();
    }

    public void Reload()
    {
        _previewsEnabled = GlobalSettingsService.Load().Previews.Enabled;
        if (!_previewsEnabled)
            _openPreviewKeys.Clear();
        var layout = ConnectionLibraryService.Load();
        _viewMode = ParseViewMode(layout.ViewMode);
        _connections.Clear();
        _connections.AddRange(ApplySavedOrder(RecentConnectionsService.LoadAll(), layout.Order));
        SetComboBoxViewMode();
        RenderConnections();
    }

    internal IReadOnlyList<ConnectionSettings> Connections => _connections;

    internal void ApplyPreviewEnabled(bool enabled)
    {
        _previewsEnabled = enabled;
        if (!enabled)
            _openPreviewKeys.Clear();

        foreach (var card in _itemsPanel.Controls.OfType<ConnectionCardControl>())
            card.PreviewEnabled = enabled;
        UpdateCardSizes();
        RefreshConnectionQuality();
    }

    internal void UpdateAvailability(string hostname, DeviceAvailabilityState state)
    {
        if (string.IsNullOrWhiteSpace(hostname)) return;
        _availabilityStates[hostname] = state;
        foreach (var card in _itemsPanel.Controls.OfType<ConnectionCardControl>())
            if (string.Equals(card.Settings.Hostname, hostname, StringComparison.OrdinalIgnoreCase))
                card.SetAvailability(state);
    }

    internal bool UpdateConnection(ConnectionSettings settings, string? previousHostname = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var previousKey = previousHostname ?? settings.Hostname ?? string.Empty;
        var index = _connections.FindIndex(connection =>
            string.Equals(connection.Hostname, previousKey, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;

        var updated = settings.Clone();
        _connections[index] = updated;
        if (!string.Equals(previousKey, updated.Hostname, StringComparison.OrdinalIgnoreCase))
            _availabilityStates.Remove(previousKey);

        var card = _itemsPanel.Controls.OfType<ConnectionCardControl>()
            .FirstOrDefault(item => string.Equals(item.Settings.Hostname, previousKey, StringComparison.OrdinalIgnoreCase));
        if (card == null) return false;

        card.UpdateSettings(updated);
        card.PreviewSource = () => PreviewSourceProvider?.Invoke(card.Settings);
        card.PreviewHost = () => PreviewHostProvider?.Invoke(card.Settings);
        if (_availabilityStates.TryGetValue(updated.Hostname ?? string.Empty, out var availability))
            card.SetAvailability(availability);
        else
            card.SetAvailability(DeviceAvailabilityState.Unknown);
        UpdateCardSizes();
        RefreshConnectionQuality();
        return true;
    }

    public void ShowStatus(string? message)
    {
        _statusLabel.Text = message ?? string.Empty;
        _statusLabel.Visible = !string.IsNullOrWhiteSpace(message);
    }

    private void RenderConnections()
    {
        ConfigureItemsPanel();
        _itemsPanel.SuspendLayout();
        try
        {
            for (var index = _itemsPanel.Controls.Count - 1; index >= 0; index--)
                _itemsPanel.Controls[index].Dispose();
            _itemsPanel.Controls.Clear();

            var query = _searchBox.Text.Trim();
            var visibleConnections = _connections
                .Where(connection => string.IsNullOrWhiteSpace(query) ||
                                     connection.DisplayName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
                                     connection.Hostname?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
                                     connection.Username?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
                                     connection.Domain?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                .ToList();

            var cardTabIndex = 10;
            foreach (var connection in visibleConnections)
            {
                var card = new ConnectionCardControl(connection)
                {
                    TabIndex = cardTabIndex++,
                    PreviewEnabled = _previewsEnabled,
                    PreviewOpen = _openPreviewKeys.Contains(PreviewKey(connection)),
                    PreviewSource = () => PreviewSourceProvider?.Invoke(connection),
                    PreviewHost = () => PreviewHostProvider?.Invoke(connection),
                };
                card.SetViewMode(_viewMode);
                if (_availabilityStates.TryGetValue(connection.Hostname ?? string.Empty, out var availability))
                    card.SetAvailability(availability);
                card.ConnectClicked += Card_ConnectClicked;
                card.PreviewToggled += Card_PreviewToggled;
                card.EditClicked += Card_EditClicked;
                card.DeleteClicked += Card_DeleteClicked;
                card.DragRequested += Card_DragRequested;
                _itemsPanel.Controls.Add(card);
            }

            _countLabel.Text = string.IsNullOrWhiteSpace(query)
                ? $"{_connections.Count} 台设备 · 拖动卡片可调整顺序"
                : $"找到 {visibleConnections.Count} 台设备 · 清空搜索后可拖动排序";

            _emptyPanel.Visible = visibleConnections.Count == 0;
            _itemsPanel.Visible = visibleConnections.Count > 0;
            UpdateCardSizes();
            RefreshConnectionQuality();
            if (_emptyPanel.Visible)
            {
                var hasConnections = _connections.Count > 0;
                _emptyTitleLabel.Text = hasConnections ? "没有匹配的设备" : "还没有保存的远程设备";
                _emptyHintLabel.Text = hasConnections
                    ? "换一个关键词试试，或清空搜索查看全部设备。"
                    : "点击右上角“新建设备”，或者连接一次设备后，它会自动出现在这里。";
            }
        }
        finally
        {
            _itemsPanel.ResumeLayout(true);
            CenterEmptyPanel();
        }
    }

    private void ConfigureItemsPanel()
    {
        _itemsPanel.FlowDirection = _viewMode == ConnectionLibraryViewMode.List
            ? FlowDirection.TopDown
            : FlowDirection.LeftToRight;
        _itemsPanel.WrapContents = _viewMode != ConnectionLibraryViewMode.List;
    }

    private void CenterEmptyPanel()
    {
        if (!_emptyPanel.Visible)
            return;

        var centerY = Math.Max(30, (_emptyPanel.ClientSize.Height - 110) / 2);
        _emptyTitleLabel.SetBounds(20, centerY, Math.Max(0, _emptyPanel.ClientSize.Width - 40), 50);
        _emptyHintLabel.SetBounds(20, centerY + 52, Math.Max(0, _emptyPanel.ClientSize.Width - 40), 55);
    }

    private void ViewModeComboBox_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_suppressViewModeEvent || _viewModeComboBox.SelectedItem is not ViewModeItem item)
            return;

        _viewMode = item.Mode;
        UpdateViewButtons();
        ConnectionLibraryService.Save(_viewMode, _connections.Select(connection => connection.Hostname ?? string.Empty));
        _itemsPanel.SuspendLayout();
        try
        {
            ConfigureItemsPanel();
            foreach (var card in _itemsPanel.Controls.OfType<ConnectionCardControl>())
                card.SetViewMode(_viewMode);
            UpdateCardSizes();
        }
        finally
        {
            _itemsPanel.ResumeLayout(true);
        }
    }

    private void SetComboBoxViewMode()
    {
        _suppressViewModeEvent = true;
        try
        {
            for (var index = 0; index < _viewModeComboBox.Items.Count; index++)
            {
                if (_viewModeComboBox.Items[index] is ViewModeItem item && item.Mode == _viewMode)
                {
                    _viewModeComboBox.SelectedIndex = index;
                    UpdateViewButtons();
                    return;
                }
            }
        }
        finally
        {
            _suppressViewModeEvent = false;
        }
    }

    private void UpdateViewButtons()
    {
        for (var i = 0; i < _viewButtons.Count; i++)
        {
            var selected = i == _viewModeComboBox.SelectedIndex;
            _viewButtons[i].Selected = selected;
            _viewButtons[i].Invalidate();
        }
    }

    private void UpdateCardSizes()
    {
        var available = Math.Max(280, _itemsPanel.ClientSize.Width - _itemsPanel.Padding.Horizontal - 20);
        var preferred = _viewMode == ConnectionLibraryViewMode.Horizontal ? 540 : _previewsEnabled ? 410 : 308;
        var columns = Math.Max(1, available / (preferred + 16));
        var width = _viewMode == ConnectionLibraryViewMode.List ? Math.Max(540, available - 16) :
            Math.Min(_viewMode == ConnectionLibraryViewMode.Horizontal ? 720 : _previewsEnabled ? 560 : 380, available / columns - 16);
        foreach (var card in _itemsPanel.Controls.OfType<ConnectionCardControl>())
            card.Width = width;
    }

    private void RefreshConnectionQuality()
    {
        if (IsDisposed || !IsHandleCreated || !Visible || ConnectionQualityProvider == null)
            return;

        foreach (var card in _itemsPanel.Controls.OfType<ConnectionCardControl>())
        {
            ConnectionQualitySnapshot? snapshot = null;
            try { snapshot = ConnectionQualityProvider(card.Settings); }
            catch (Exception) { }
            card.SetConnectionQuality(snapshot);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _qualityRefreshTimer.Stop();
            _qualityRefreshTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ItemsPanel_DragEnter(object? sender, DragEventArgs e)
    {
        if (_draggedCard != null && e.Data?.GetDataPresent(typeof(ConnectionCardControl)) == true)
            e.Effect = DragDropEffects.Move;
    }

    private void ItemsPanel_DragOver(object? sender, DragEventArgs e)
    {
        if (_draggedCard == null || e.Data?.GetDataPresent(typeof(ConnectionCardControl)) != true)
            return;

        e.Effect = DragDropEffects.Move;
        MoveDraggedCard(e);
    }

    private void ItemsPanel_DragDrop(object? sender, DragEventArgs e)
    {
        if (_draggedCard == null)
            return;

        RebuildOrderFromCards();
        SaveOrder();
        _draggedCard = null;
    }

    private void MoveDraggedCard(DragEventArgs e)
    {
        var point = _itemsPanel.PointToClient(new Point(e.X, e.Y));
        var target = _itemsPanel.GetChildAtPoint(point);
        if (target is not ConnectionCardControl targetCard || targetCard == _draggedCard)
            return;

        var targetIndex = _itemsPanel.Controls.GetChildIndex(targetCard);
        if (_viewMode is ConnectionLibraryViewMode.Cards or ConnectionLibraryViewMode.Horizontal)
        {
            var afterTarget = _viewMode == ConnectionLibraryViewMode.Horizontal
                ? point.X > targetCard.Left + targetCard.Width / 2
                : point.Y > targetCard.Top + targetCard.Height / 2;
            if (afterTarget)
                targetIndex++;
        }

        var draggedCard = _draggedCard;
        if (draggedCard == null)
            return;

        var currentIndex = _itemsPanel.Controls.GetChildIndex(draggedCard);
        if (currentIndex != targetIndex)
            _itemsPanel.Controls.SetChildIndex(draggedCard, Math.Min(targetIndex, _itemsPanel.Controls.Count - 1));
    }

    private void Card_DragRequested(ConnectionCardControl card)
    {
        if (!string.IsNullOrWhiteSpace(_searchBox.Text))
            return;

        _draggedCard = card;
        try
        {
            card.DoDragDrop(card, DragDropEffects.Move);
        }
        finally
        {
            if (_draggedCard != null)
            {
                RebuildOrderFromCards();
                SaveOrder();
            }
            _draggedCard = null;
        }
    }

    private void RebuildOrderFromCards()
    {
        _connections.Clear();
        _connections.AddRange(_itemsPanel.Controls
            .OfType<ConnectionCardControl>()
            .Select(card => card.Settings));
    }

    private void SaveOrder()
    {
        ConnectionLibraryService.Save(_viewMode, _connections.Select(connection => connection.Hostname ?? string.Empty));
        _countLabel.Text = $"{_connections.Count} 台设备 · 拖动卡片可调整顺序";
    }

    private void Card_ConnectClicked(ConnectionCardControl card)
        => ConnectRequested?.Invoke(card.Settings.Clone());

    private void Card_PreviewToggled(ConnectionCardControl card, bool isOpen)
    {
        var key = PreviewKey(card.Settings);
        if (isOpen)
            _openPreviewKeys.Add(key);
        else
            _openPreviewKeys.Remove(key);
        PreviewToggledRequested?.Invoke(card.Settings.Clone(), isOpen);
    }

    private static string PreviewKey(ConnectionSettings settings)
        => $"{settings.Hostname}\n{settings.Domain}\n{settings.Username}";

    private void Card_EditClicked(ConnectionCardControl card)
        => EditRequested?.Invoke(card.Settings.Clone());

    private void Card_DeleteClicked(ConnectionCardControl card)
        => DeleteRequested?.Invoke(card.Settings.Clone());

    private static IEnumerable<ConnectionSettings> ApplySavedOrder(
        IEnumerable<ConnectionSettings> connections,
        IReadOnlyList<string> savedOrder)
    {
        var source = connections.ToList();
        var rank = savedOrder
            .Select((hostname, index) => new { hostname, index })
            .ToDictionary(item => item.hostname, item => item.index, StringComparer.OrdinalIgnoreCase);

        return source
            .OrderBy(connection => rank.TryGetValue(connection.Hostname ?? string.Empty, out var index)
                ? index
                : int.MaxValue)
            .ThenByDescending(connection => connection.LastConnected);
    }

    private static ConnectionLibraryViewMode ParseViewMode(string? value)
        => Enum.TryParse<ConnectionLibraryViewMode>(value, true, out var mode)
            ? mode
            : ConnectionLibraryViewMode.Cards;

    private sealed class HeaderTextLabel : Label
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            // Default Label adds font-dependent side bearings; larger titles
            // otherwise appear indented relative to the smaller headings.
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding |
                TextFormatFlags.SingleLine);
        }
    }

    private sealed record ViewModeItem(ConnectionLibraryViewMode Mode, string Text)
    {
        public override string ToString() => Text;
    }
}






