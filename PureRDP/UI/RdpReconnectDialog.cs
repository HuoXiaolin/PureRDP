using System;
using System.Drawing;
using System.Windows.Forms;

namespace PureRDP.UI;

internal sealed class RdpReconnectDialog : Form
{
    private readonly Label _statusLabel;
    private readonly Label _detailLabel;
    private readonly Label _countdownLabel;
    private readonly ProgressBar _progressBar;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Button _cancelButton;
    private int _attempt;
    private int _remainingSeconds;
    private int _delaySeconds;
    private bool _closingFromOwner;
    private bool _cancelRaised;

    internal event Action<int>? RetryRequested;
    internal event EventHandler? CancelRequested;

    internal RdpReconnectDialog(string target)
    {
        Text = "PureRDP - 自动重连";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;
        ShowIcon = false;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(470, 248);
        BackColor = UiDesignTokens.Surface;
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        var accent = new Panel
        {
            BackColor = UiDesignTokens.Accent,
            Dock = DockStyle.Top,
            Height = 4,
        };
        Controls.Add(accent);

        var iconBadge = new Panel
        {
            BackColor = UiDesignTokens.AccentSoft,
            Location = new Point(24, 28),
            Size = new Size(46, 46),
        };
        var icon = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = "↻",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI Symbol", 24F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = UiDesignTokens.Accent,
        };
        iconBadge.Controls.Add(icon);
        Controls.Add(iconBadge);

        var heading = new Label
        {
            AutoSize = true,
            Location = new Point(84, 28),
            Text = "连接中断，正在自动重试",
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = UiDesignTokens.TextPrimary,
        };
        Controls.Add(heading);

        var targetLabel = new Label
        {
            AutoSize = false,
            Location = new Point(84, 56),
            Size = new Size(350, 22),
            Text = $"连接目标：{target}",
            ForeColor = UiDesignTokens.TextSecondary,
            AutoEllipsis = true,
        };
        Controls.Add(targetLabel);

        _statusLabel = new Label
        {
            AutoSize = false,
            Location = new Point(26, 98),
            Size = new Size(415, 24),
            Text = "正在准备重试…",
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = UiDesignTokens.TextPrimary,
        };
        Controls.Add(_statusLabel);

        _detailLabel = new Label
        {
            AutoSize = false,
            Location = new Point(26, 127),
            Size = new Size(415, 22),
            Text = "自动重试最多 10 次；取消或关闭此窗口将关闭当前连接。",
            ForeColor = UiDesignTokens.TextMuted,
        };
        Controls.Add(_detailLabel);

        _progressBar = new ProgressBar
        {
            Location = new Point(26, 161),
            Size = new Size(415, 10),
            Minimum = 0,
            Maximum = 2,
            Value = 0,
            Style = ProgressBarStyle.Continuous,
        };
        Controls.Add(_progressBar);

        _countdownLabel = new Label
        {
            AutoSize = false,
            Location = new Point(26, 177),
            Size = new Size(220, 20),
            Text = string.Empty,
            ForeColor = UiDesignTokens.TextSecondary,
        };
        Controls.Add(_countdownLabel);

        _cancelButton = new ModernButton
        {
            Location = new Point(315, 204),
            Size = new Size(126, 36),
            Text = "取消并关闭",
            FlatStyle = FlatStyle.Flat,
            BackColor = UiDesignTokens.SurfaceSubtle,
            ForeColor = UiDesignTokens.TextSecondary,
            UseVisualStyleBackColor = false,
        };
        _cancelButton.FlatAppearance.BorderColor = UiDesignTokens.Border;
        _cancelButton.Click += (_, _) => RaiseCancelRequested();
        Controls.Add(_cancelButton);
        CancelButton = _cancelButton;

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += Timer_Tick;
    }

    internal void WaitForRetry(int attempt, int delaySeconds, string? detail)
    {
        _timer.Stop();
        _attempt = attempt;
        _delaySeconds = Math.Max(1, delaySeconds);
        _remainingSeconds = _delaySeconds;
        _statusLabel.Text = $"第 {attempt} / 10 次重试将在 {FormatDuration(_remainingSeconds)} 后开始";
        _detailLabel.Text = string.IsNullOrWhiteSpace(detail)
            ? "重试间隔逐次翻倍；取消或关闭此窗口将关闭当前连接。"
            : Clip(detail.Trim(), 100);
        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.Maximum = _delaySeconds;
        _progressBar.Value = 0;
        _countdownLabel.Text = $"下一次尝试：{FormatDuration(_remainingSeconds)} 后";
        _timer.Start();
    }

    internal void ShowConnecting(int attempt)
    {
        _timer.Stop();
        _statusLabel.Text = $"正在执行第 {attempt} / 10 次重试…";
        _detailLabel.Text = "正在与远程计算机建立会话，连接成功后此窗口会自动关闭。";
        _progressBar.Style = ProgressBarStyle.Marquee;
        _countdownLabel.Text = string.Empty;
    }

    internal void CloseFromOwner()
    {
        if (IsDisposed) return;
        _closingFromOwner = true;
        _timer.Stop();
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_closingFromOwner && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            if (!_cancelRaised)
            {
                _cancelRaised = true;
                try
                {
                    BeginInvoke(new Action(() => CancelRequested?.Invoke(this, EventArgs.Empty)));
                }
                catch (InvalidOperationException)
                {
                    CancelRequested?.Invoke(this, EventArgs.Empty);
                }
            }
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _remainingSeconds = Math.Max(0, _remainingSeconds - 1);
        _progressBar.Value = Math.Min(_progressBar.Maximum, _delaySeconds - _remainingSeconds);
        _statusLabel.Text = $"第 {_attempt} / 10 次重试将在 {FormatDuration(_remainingSeconds)} 后开始";
        _countdownLabel.Text = _remainingSeconds == 0
            ? "正在启动连接…"
            : $"下一次尝试：{FormatDuration(_remainingSeconds)} 后";

        if (_remainingSeconds != 0) return;
        _timer.Stop();
        RetryRequested?.Invoke(_attempt);
    }

    private void RaiseCancelRequested()
    {
        if (_cancelRaised) return;
        _cancelRaised = true;
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private static string FormatDuration(int seconds)
        => seconds >= 60 ? $"{seconds / 60} 分 {seconds % 60} 秒" : $"{seconds} 秒";

    private static string Clip(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "…";
}
