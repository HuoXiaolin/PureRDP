using AxMSTSCLib;
using PureRDP.Platform;
using PureRDP.UI;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace PureRDP.Services;

/// <summary>
/// Hosts the native RDP ActiveX control, routing window gestures through the
/// same custom path as nested HWND input instead of native modal sizing.
/// </summary>
internal sealed class RdpClientWindowHost : AxMsRdpClient10NotSafeForScripting
{
    protected override void WndProc(ref Message m)
    {
        if (FindForm() is FormRdpClient form)
        {
            if (form.TryHandleWindowMoveInput(ref m)) return;
            if (form.TryHandleWindowResizeInput(ref m)) return;
            var screenPoint = IsMouseMessage(m.Msg)
                ? GetScreenPoint(m)
                : Cursor.Position;

            // Guard stale messages queued before the overlay was expanded.
            // Toolbar bounds must never become native resize/capture input.
            if (form.IsToolbarArea(screenPoint))
            {
                if (m.Msg == Interop.WM_NCHITTEST)
                {
                    m.Result = (IntPtr)Interop.HTCLIENT;
                    return;
                }
                if (m.Msg is Interop.WM_NCLBUTTONDOWN or Interop.WM_NCLBUTTONUP or Interop.WM_NCLBUTTONDBLCLK)
                {
                    form.EndRemoteWindowResize();
                    return;
                }
                base.WndProc(ref m);
                return;
            }

            // Keep non-client hit testing out of DefWindowProc. Edge presses
            // below and in the queue filter arm our custom resize instead.
            if (m.Msg == Interop.WM_NCHITTEST && form.IsRemoteWindowResizable)
            {
                m.Result = (IntPtr)Interop.HTCLIENT;
                return;
            }

            var hitTest = form.GetRemoteResizeHitTest(screenPoint);
            if ((m.Msg is Interop.WM_LBUTTONDOWN or Interop.WM_NCLBUTTONDOWN) && hitTest != Interop.HTCLIENT)
            {
                form.BeginRemoteWindowResize(hitTest, screenPoint);
                return;
            }
        }

        base.WndProc(ref m);
    }

    private static bool IsMouseMessage(int message)
        => message is Interop.WM_NCHITTEST or Interop.WM_NCLBUTTONDOWN or Interop.WM_NCLBUTTONUP or
            Interop.WM_NCLBUTTONDBLCLK or Interop.WM_LBUTTONDOWN or Interop.WM_LBUTTONUP or
            Interop.WM_MOUSEMOVE or 0x0203; // WM_LBUTTONDBLCLK

    private static Point GetScreenPoint(Message message)
    {
        var packed = message.LParam.ToInt64();
        var point = new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff));
        if (message.Msg is Interop.WM_NCHITTEST or Interop.WM_NCLBUTTONDOWN or Interop.WM_NCLBUTTONUP or Interop.WM_NCLBUTTONDBLCLK)
            return point;
        return WindowChromeInterop.ClientToScreen(message.HWnd, ref point) ? point : Cursor.Position;
    }
}
