using System;
using System.Drawing;
using System.Windows.Forms;

namespace PureRDP.UI;

// An in-session surface, never a top-level or modal window.
internal sealed class RdpReconnectDialog : UserControl
{
    private readonly CrystalLoaderControl _loader = new();
    private readonly Label _heading = new();
    private readonly Label _targetLabel = new();
    private readonly Label _statusLabel = new();
    private readonly Label _detailLabel = new();
    private readonly Label _countdownLabel = new();
    private readonly Button _cancelButton = new ModernButton();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private int _attempt;
    private int _remainingSeconds;
    private bool _cancelRaised;
    internal event Action<int>? RetryRequested;
    internal event EventHandler? CancelRequested;

    internal RdpReconnectDialog(string target)
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        Size = new Size(800, 600);
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(247, 250, 253);
        Font = new Font("Microsoft YaHei UI", 10F);
        _loader.BackColor = BackColor;
        _heading.Text = "连接中断，正在自动重试";
        _heading.Font = new Font(Font, FontStyle.Bold);
        _targetLabel.Text = $"连接目标：{target}";
        _statusLabel.Text = "正在准备重试…";
        _detailLabel.Text = "最多尝试 10 次，等待时间逐次翻倍。";
        foreach (var label in new[] { _heading, _targetLabel, _statusLabel, _detailLabel, _countdownLabel })
        {
            label.TextAlign = ContentAlignment.MiddleCenter;
            label.AutoEllipsis = true;
            label.ForeColor = label == _detailLabel ? UiDesignTokens.TextMuted : UiDesignTokens.TextPrimary;
            Controls.Add(label);
        }
        _cancelButton.Text = "取消并关闭";
        _cancelButton.Click += (_, _) => RaiseCancelRequested();
        Controls.Add(_cancelButton);
        Controls.Add(_loader);
        _timer.Tick += Timer_Tick;
        Resize += (_, _) => LayoutSurface();
        LayoutSurface();
        _loader.SetLoading(true);
    }

    private void LayoutSurface()
    {
        if (_loader == null || _cancelButton == null) return;
        var scale = DeviceDpi / 96F;
        var margin = Math.Min(24, Math.Max(4, ClientSize.Width / 12));
        var line = Math.Max(18, (int)(26 * scale));
        var buttonHeight = Math.Max(24, (int)(36 * scale));
        var loaderSize = Math.Max(0, Math.Min((int)(200 * scale), ClientSize.Height - 5 * line - buttonHeight - 24));
        var total = loaderSize + 5 * line + buttonHeight + 12;
        var top = Math.Max(0, (ClientSize.Height - total) / 2);
        _loader.SetBounds((ClientSize.Width - loaderSize) / 2, top, loaderSize, loaderSize);
        top += loaderSize;
        foreach (var label in new[] { _heading, _targetLabel, _statusLabel, _detailLabel, _countdownLabel })
        {
            label.SetBounds(margin, top, Math.Max(0, ClientSize.Width - margin * 2), line);
            top += line;
        }
        var buttonWidth = Math.Min(Math.Max(0, ClientSize.Width - margin * 2), (int)(132 * scale));
        _cancelButton.SetBounds((ClientSize.Width - buttonWidth) / 2, top + 8, buttonWidth, buttonHeight);
    }

    internal void WaitForRetry(int attempt, int delaySeconds, string? detail)
    {
        _timer.Stop();
        _attempt = attempt;
        _remainingSeconds = Math.Max(1, delaySeconds);
        _detailLabel.Text = string.IsNullOrWhiteSpace(detail)
            ? "重试间隔逐次翻倍；取消将关闭当前连接。" : Clip(detail.Trim(), 100);
        UpdateCountdown();
        Show();
        BringToFront();
        _loader.SetLoading(true);
        _timer.Start();
    }

    internal void ShowConnecting(int attempt)
    {
        _timer.Stop();
        _statusLabel.Text = $"正在执行第 {attempt} / 10 次重试…";
        _detailLabel.Text = "正在与远程计算机建立会话…";
        _countdownLabel.Text = string.Empty;
        Show();
        BringToFront();
        _loader.SetLoading(true);
    }

    internal void CloseFromOwner()
    {
        if (IsDisposed) return;
        _timer.Stop();
        _loader.SetLoading(false);
        Hide();
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape) { RaiseCancelRequested(); return true; }
        return base.ProcessDialogKey(keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Stop(); _timer.Dispose(); }
        base.Dispose(disposing);
    }

    private void UpdateCountdown()
    {
        _statusLabel.Text = $"第 {_attempt} / 10 次重试将在 {FormatDuration(_remainingSeconds)} 后开始";
        _countdownLabel.Text = _remainingSeconds == 0 ? "正在启动连接…" : $"下一次尝试：{FormatDuration(_remainingSeconds)} 后";
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _remainingSeconds = Math.Max(0, _remainingSeconds - 1);
        UpdateCountdown();
        if (_remainingSeconds != 0) return;
        _timer.Stop();
        RetryRequested?.Invoke(_attempt);
    }

    private void RaiseCancelRequested()
    {
        if (_cancelRaised) return;
        _cancelRaised = true;
        _timer.Stop();
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private static string FormatDuration(int seconds)
        => seconds >= 60 ? $"{seconds / 60} 分 {seconds % 60} 秒" : $"{seconds} 秒";
    private static string Clip(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "…";
}
