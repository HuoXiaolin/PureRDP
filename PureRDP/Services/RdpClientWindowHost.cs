using AxMSTSCLib;
using PureRDP.Platform;
using PureRDP.UI;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace PureRDP.Services;

/// <summary>
/// Hosts the native RDP ActiveX control while forwarding the border hit-test
/// needed by a borderless PureRDP window. The remote desktop remains flush
/// with the window edge; no visible resize rail is required.
/// </summary>
internal sealed class RdpClientWindowHost : AxMsRdpClient10NotSafeForScripting
{
    protected override void WndProc(ref Message m)
    {
        if (FindForm() is FormRdpClient form)
        {
            var screenPoint = IsMouseMessage(m.Msg)
                ? GetScreenPoint(m)
                : Cursor.Position;

            // The toolbar is a separate top-level overlay. Its transparent
            // padding can still cause the native RDP child to receive the
            // message at the screen edge; never let that padding enter the
            // resize path.
            if (form.IsToolbarArea(screenPoint))
            {
                // The transparent padding is intentionally click-through, but
                // it must be client input. If the ActiveX child reports a
                // non-client hit at the screen edge, Windows starts a native
                // resize/capture gesture and subsequent remote clicks only
                // produce the system beep until the window is recreated.
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

            // The ActiveX control receives mouse messages before the
            // borderless Form does. Use screen coordinates so the hit test is
            // independent of the host's client-area origin and DPI scaling.
            var borderlessHitTest = form.GetBorderlessWindowHitTest(screenPoint);
            if (m.Msg == Interop.WM_NCHITTEST && borderlessHitTest != Interop.HTCLIENT)
            {
                m.Result = (IntPtr)borderlessHitTest;
                return;
            }

            if ((m.Msg is Interop.WM_LBUTTONDOWN or Interop.WM_NCLBUTTONDOWN) &&
                borderlessHitTest == Interop.HTCAPTION)
            {
                // The native RDP child receives the click instead of the
                // borderless parent. Forward caption dragging to the parent
                // so showing the floating control bar cannot lock movement.
                WindowChromeInterop.ReleaseCapture();
                WindowChromeInterop.SendMessage(form.Handle, Interop.WM_NCLBUTTONDOWN,
                    (IntPtr)Interop.HTCAPTION, IntPtr.Zero);
                return;
            }

            var hitTest = form.GetRemoteResizeHitTest(screenPoint);
            if ((m.Msg is Interop.WM_LBUTTONDOWN or Interop.WM_NCLBUTTONDOWN) && hitTest != Interop.HTCLIENT)
            {
                form.BeginRemoteWindowResize(hitTest);
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
