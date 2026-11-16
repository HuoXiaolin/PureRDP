using PureRDP.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PureRDP.UI;

internal sealed class LanDiscoveryDialog : Form
{
    private readonly TextBox _range = new()
    {
        AccessibleName = "扫描 IPv4 地址段",
        PlaceholderText = "例如 192.168.1.0/24",
        MaxLength = 64,
    };
    private readonly TextBox _port = new()
    {
        AccessibleName = "扫描 TCP 端口",
        Text = LanDiscoveryService.DefaultPort.ToString(CultureInfo.InvariantCulture),
        MaxLength = 5,
    };
    private readonly ComboBox _networks = new ModernComboBox
    {
        AccessibleName = "选择网卡 IPv4 网段",
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList,
        Margin = new Padding(0, 3, 10, 0),
    };
    private readonly ModernButton _quickScan = new() { Text = "快速扫描", Kind = ModernButtonKind.Primary };
    private readonly ModernButton _scan = new() { Text = "扫描指定范围" };
    private readonly ModernButton _temporary = new() { Text = "临时连接", Enabled = false, Width = 100 };
    private readonly ModernButton _save = new() { Text = "保存为新设备…", Enabled = false, Width = 136 };
    private readonly ProgressBar _progress = new() { AccessibleName = "扫描进度", Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly Label _status = new()
    {
        Text = "快速扫描使用当前网卡的 IPv4 网段。",
        AutoEllipsis = true,
        Dock = DockStyle.Fill,
        Margin = Padding.Empty,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = UiDesignTokens.TextSecondary,
    };
    private readonly DataGridView _results;
    private readonly HashSet<string> _savedEndpoints;
    private readonly Func<LanScanNetwork[]> _readNetworks;
    private readonly Func<IPAddress, bool>? _localAddressOverride;
    private CancellationTokenSource? _activeScan;

    internal string? SelectedAddress { get; private set; }
    internal bool ConnectTemporarily { get; private set; }

    internal LanDiscoveryDialog(Icon? icon, IEnumerable<string?> savedAddresses)
        : this(icon, savedAddresses, readNetworks: null, localAddressOverride: null) { }

    internal LanDiscoveryDialog(Icon? icon, IEnumerable<string?> savedAddresses,
        Func<LanScanNetwork[]>? readNetworks, Func<IPAddress, bool>? localAddressOverride)
    {
        _readNetworks = readNetworks ?? LanDiscoveryService.GetAdapterNetworks;
        _localAddressOverride = localAddressOverride;
        _savedEndpoints = savedAddresses.Select(LanDiscoveryService.NormalizeEndpoint).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Text = "局域网扫描 - PureRDP";
        Icon = icon;
        Font = new Font(UiDesignTokens.FontFamily, 9F);
        BackColor = UiDesignTokens.PageBackground;
        ForeColor = UiDesignTokens.TextPrimary;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(660, 506);
        MinimumSize = new Size(575, 445);
        RefreshNetworks();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(20, 14, 20, 12),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void AddRow(Control control, SizeType sizeType, float height)
        {
            var row = layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(sizeType, height));
            layout.Controls.Add(control, 0, row);
        }

        var inputs = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 5, Margin = Padding.Empty };
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        inputs.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        inputs.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        inputs.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
        inputs.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        inputs.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        inputs.Controls.Add(new Label { Text = "网卡 IPv4 网段", AutoSize = true, Margin = Padding.Empty }, 0, 0);
        inputs.Controls.Add(new Label { Text = "TCP 端口", AutoSize = true, Margin = Padding.Empty }, 1, 0);
        inputs.Controls.Add(_networks, 0, 1);
        inputs.Controls.Add(new SettingsTextField(_port) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 10, 0), TabIndex = 1 }, 1, 1);
        _quickScan.Dock = DockStyle.Fill;
        _quickScan.Margin = Padding.Empty;
        _quickScan.TabIndex = 2;
        inputs.Controls.Add(_quickScan, 2, 1);
        inputs.Controls.Add(new Label { Text = "指定 IPv4 地址段", AutoSize = true, Margin = Padding.Empty }, 0, 3);
        var rangeField = new SettingsTextField(_range) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 10, 0), TabIndex = 3 };
        inputs.Controls.Add(rangeField, 0, 4);
        inputs.SetColumnSpan(rangeField, 2);
        _scan.Dock = DockStyle.Fill;
        _scan.Margin = Padding.Empty;
        _scan.TabIndex = 4;
        inputs.Controls.Add(_scan, 2, 4);
        AddRow(inputs, SizeType.Absolute, 126);
        AddRow(new Label
        {
            Text = "支持 CIDR 或起止地址（如 192.168.1.10-50）。结果只表示端口开放。",
            Dock = DockStyle.Fill, Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiDesignTokens.TextSecondary,
        }, SizeType.Absolute, 34);

        _results = new DataGridView
        {
            AccessibleName = "端口开放的设备",
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BackgroundColor = UiDesignTokens.Surface,
            GridColor = UiDesignTokens.Border,
            BorderStyle = BorderStyle.FixedSingle,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCellsExceptHeaders,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            ScrollBars = ScrollBars.Vertical,
            MultiSelect = false,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            EnableHeadersVisualStyles = false,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = UiDesignTokens.Surface, ForeColor = UiDesignTokens.TextSecondary,
                SelectionBackColor = UiDesignTokens.Surface, SelectionForeColor = UiDesignTokens.TextSecondary,
            },
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Padding = new Padding(7, 4, 7, 4),
                BackColor = UiDesignTokens.Surface, ForeColor = UiDesignTokens.TextPrimary,
                SelectionBackColor = UiDesignTokens.SurfacePressed, SelectionForeColor = UiDesignTokens.TextPrimary,
            },
        };
        _results.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "IP 地址", FillWeight = 56, SortMode = DataGridViewColumnSortMode.NotSortable });
        _results.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "端口", FillWeight = 18, SortMode = DataGridViewColumnSortMode.NotSortable });
        _results.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "设备库", FillWeight = 26, SortMode = DataGridViewColumnSortMode.NotSortable });
        AddRow(_results, SizeType.Percent, 100);
        AddRow(_status, SizeType.Absolute, 30);
        AddRow(_progress, SizeType.Absolute, 8);

        var close = new ModernButton { Text = "关闭", Width = 74, DialogResult = DialogResult.Cancel };
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(0, 10, 0, 0),
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
        };
        foreach (var button in new[] { _save, _temporary, close })
        {
            button.Height = UiDesignTokens.ControlHeight;
            button.Margin = new Padding(8, 0, 0, 0);
            footer.Controls.Add(button);
        }
        AddRow(footer, SizeType.Absolute, 49);
        Controls.Add(layout);
        AcceptButton = _quickScan;
        CancelButton = close;
        _quickScan.Click += Scan_Click;
        _scan.Click += Scan_Click;
        _results.SelectionChanged += (_, _) => UpdateActions();
        _save.Click += (_, _) => ChooseResult(temporary: false);
        _temporary.Click += (_, _) => ChooseResult(temporary: true);
        _range.Enter += (_, _) => AcceptButton = _scan;
        _range.Leave += (_, _) => AcceptButton = _quickScan;
        ActiveControl = _quickScan;
    }

    private string? CurrentAddress => _results.SelectedRows.Count == 1 ? _results.SelectedRows[0].Tag as string : null;

    private void UpdateActions()
    {
        var endpoint = CurrentAddress;
        _temporary.Enabled = endpoint != null;
        _save.Enabled = endpoint != null && !_savedEndpoints.Contains(endpoint);
    }

    private void ChooseResult(bool temporary)
    {
        if (CurrentAddress is not { } endpoint || (!temporary && _savedEndpoints.Contains(endpoint))) return;
        SelectedAddress = endpoint;
        ConnectTemporarily = temporary;
        DialogResult = DialogResult.OK;
    }

    private async void Scan_Click(object? sender, EventArgs e)
    {
        if (_activeScan != null)
        {
            _activeScan.Cancel();
            return;
        }

        if (!int.TryParse(_port.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
        {
            ShowStatus("TCP 端口必须是 1 到 65535 之间的整数。", isError: true);
            _port.Focus();
            return;
        }

        LanScanScope[] scopes;
        var quick = ReferenceEquals(sender, _quickScan);
        if (quick)
        {
            var previous = _networks.SelectedItem as LanScanNetwork;
            var networks = RefreshNetworks();
            var selected = _networks.SelectedItem as LanScanNetwork;
            if (previous != null && selected == null)
            {
                ShowStatus("所选网卡的 IPv4 网段已不可用，请重新选择。", isError: true);
                return;
            }
            scopes = (selected != null ? new[] { selected } : networks)
                .Select(network => new LanScanScope(network.ScanRange, network.LocalAddress))
                .DistinctBy(scope => scope.Range).ToArray();
            if (scopes.Length == 0)
            {
                ShowStatus("未检测到活动的 IPv4 网段，可以手动指定范围。", isError: true);
                return;
            }
        }
        else
        {
            if (!LanDiscoveryService.TryParseRange(_range.Text, out var range, out var error))
            {
                ShowStatus(error, isError: true);
                _range.Focus();
                return;
            }
            scopes = [new LanScanScope(range)];
        }
        await RunScanAsync(scopes, port, quick ? _quickScan : _scan);
    }

    private LanScanNetwork[] RefreshNetworks()
    {
        var previous = _networks.SelectedItem as LanScanNetwork;
        var networks = _readNetworks();
        var selected = networks.FirstOrDefault(network => network == previous)
            ?? networks.FirstOrDefault(network => network.AdapterId == previous?.AdapterId);
        _networks.BeginUpdate();
        try
        {
            _networks.Items.Clear();
            _networks.Items.Add(networks.Length == 0 ? "未检测到活动网段" : $"全部活动网段（{networks.Length}）");
            _networks.Items.AddRange(networks);
            _networks.SelectedIndex = selected == null ? 0 : Array.IndexOf(networks, selected) + 1;
        }
        finally { _networks.EndUpdate(); }
        return networks;
    }

    private async Task RunScanAsync(LanScanScope[] scopes, int port, ModernButton activeButton)
    {
        var isLocalAddress = _localAddressOverride ?? LanDiscoveryService.CreateLocalAddressFilter();
        using var cancellation = new CancellationTokenSource();
        _activeScan = cancellation;
        var total = scopes.Sum(scope => scope.Range.Count);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _results.Rows.Clear();
        UpdateActions();
        _progress.Value = 0;
        _progress.Maximum = total;
        SetScanning(activeButton, true);
        ShowStatus($"正在扫描 {scopes.Length} 个网段，0 / {total} 个地址…");
        try
        {
            var checkedInPreviousNetworks = 0;
            foreach (var scope in scopes)
            {
                await foreach (var batch in LanDiscoveryService.ScanAsync(scope, port, isLocalAddress, cancellation.Token))
                {
                    if (!CanUpdateScanUi) return;
                    foreach (var address in batch.ReachableAddresses)
                    {
                        var endpoint = LanDiscoveryService.FormatEndpoint(address, port);
                        if (!found.Add(endpoint)) continue;
                        var row = _results.Rows.Add(address.ToString(), port.ToString(CultureInfo.InvariantCulture),
                            _savedEndpoints.Contains(endpoint) ? "已保存" : "未保存");
                        _results.Rows[row].Tag = endpoint;
                    }
                    _progress.Value = checkedInPreviousNetworks + batch.CheckedCount;
                    ShowStatus($"正在扫描 {_progress.Value} / {total} 个地址，已发现 {_results.RowCount} 台。");
                    UpdateActions();
                }
                checkedInPreviousNetworks += scope.Range.Count;
            }
            if (CanUpdateScanUi)
                ShowStatus(_results.RowCount == 0 ? "扫描完成，未发现其他设备的开放端口。" : $"扫描完成，发现 {_results.RowCount} 台设备。");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (CanUpdateScanUi) ShowStatus($"扫描已停止，保留已发现的 {_results.RowCount} 台设备。");
        }
        finally
        {
            _activeScan = null;
            if (CanUpdateScanUi) SetScanning(activeButton, false);
        }
    }

    private bool CanUpdateScanUi => !IsDisposed && !Disposing && Visible && DialogResult == DialogResult.None;

    private void SetScanning(ModernButton activeButton, bool scanning)
    {
        _networks.Enabled = _range.Enabled = _port.Enabled = !scanning;
        _quickScan.Enabled = !scanning || activeButton == _quickScan;
        _scan.Enabled = !scanning || activeButton == _scan;
        _quickScan.Text = scanning && activeButton == _quickScan ? "停止扫描" : "快速扫描";
        _scan.Text = scanning && activeButton == _scan ? "停止扫描" : "扫描指定范围";
        AcceptButton = scanning ? activeButton : _quickScan;
    }

    private void ShowStatus(string text, bool isError = false)
    {
        _status.Text = text;
        _status.ForeColor = isError ? UiDesignTokens.Danger : UiDesignTokens.TextSecondary;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (!e.Cancel) _activeScan?.Cancel();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _activeScan?.Cancel();
        base.Dispose(disposing);
    }
}
