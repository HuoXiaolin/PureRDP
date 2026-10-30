using System;
using System.Drawing;
using System.Windows.Forms;
namespace PureRDP.UI;

internal static class ConnectionListColumns
{
    internal static int[] Widths(int contentWidth, bool quality) {
        var budget = Math.Max(1, contentWidth - (quality ? 32 : 24));
        var name = budget * 24 / 100;
        var host = budget * (quality ? 25 : 32) / 100;
        var user = budget * (quality ? 17 : 20) / 100;
        var recent = quality ? budget * 18 / 100 : budget - name - host - user;
        return new[] { name, host, user, recent, quality ? budget - name - host - user - recent : 0 };
    }
}

internal sealed class ConnectionListHeader : Control
{
    internal int ItemWidth;
    internal ConnectionListHeader() {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = UiDesignTokens.PageBackground;
        Height = 36; Dock = DockStyle.Top; TabStop = false;
        AccessibleName = "设备列表列标题";
    }
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);
        var scale = DeviceDpi / 96.0;
        int Px(int n) => (int)Math.Round(n * scale);
        var cardWidth = Math.Max(UiDesignTokens.ListCardMinimumWidth, (int)((ItemWidth > 0 ? ItemWidth : Width - Px(44)) / scale));
        var contentWidth = cardWidth - 246 - 66;
        var quality = contentWidth >= 610;
        var widths = ConnectionListColumns.Widths(contentWidth, quality);
        var x = 22 + 54;
        var titles = new[] { "设备名称", "地址 / 端口", "用户名", "上次连接", "连接质量" };
        for (var i = 0; i < titles.Length; i++) {
            if (widths[i] <= 0) continue;
            TextRenderer.DrawText(e.Graphics, titles[i], Font, new Rectangle(Px(x), 0, Px(widths[i]), Height),
                UiDesignTokens.TextSecondary, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            x += widths[i] + 8;
        }
        TextRenderer.DrawText(e.Graphics, "操作", Font,
            new Rectangle(Px(cardWidth - 246 + 22), 0, Px(168), Height), UiDesignTokens.TextSecondary,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}
