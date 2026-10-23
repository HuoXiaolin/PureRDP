using System;
using System.Windows.Forms;
using PureRDP.Platform;

namespace PureRDP.UI.Services;

// ActiveX creates nested native HWNDs that bypass the AxHost WndProc.
// Intercept queued mouse input before dispatch to any descendant window.
internal sealed class RemoteResizeInputFilter(FormRdpClient form) : IMessageFilter, IDisposable
{
    public bool PreFilterMessage(ref Message m)
    {
        if (!form.IsHandleCreated || !form.IsRemoteWindowResizable ||
            (m.HWnd != form.Handle && !WindowChromeInterop.IsChild(form.Handle, m.HWnd)))
            return false;

        if (form.TryHandleWindowMoveInput(ref m)) return true;
        if (form.TryHandleWindowResizeInput(ref m)) return true;

        if (m.Msg != Interop.WM_LBUTTONDOWN && m.Msg != Interop.WM_NCLBUTTONDOWN &&
            m.Msg != Interop.WM_LBUTTONUP && m.Msg != Interop.WM_NCLBUTTONUP &&
            m.Msg != Interop.WM_MOUSEMOVE)
            return false;

        var packed = m.LParam.ToInt64();
        var point = new System.Drawing.Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff));
        if (m.Msg is not Interop.WM_NCLBUTTONDOWN and not Interop.WM_NCLBUTTONUP &&
            !WindowChromeInterop.ClientToScreen(m.HWnd, ref point))
            return false;

        // A click on an edge is intentionally not a resize until the pointer
        // moves past the drag threshold. Finish that pending gesture as soon
        // as the release reaches the ActiveX child, so it cannot leave the
        // host in a non-clickable resize state.
        if (m.Msg is Interop.WM_LBUTTONUP or Interop.WM_NCLBUTTONUP)
        {
            form.EndRemoteWindowResize();
            // A stale non-client release must not complete a native edge
            // gesture in a nested ActiveX window under the toolbar.
            return m.Msg == Interop.WM_NCLBUTTONUP && form.IsToolbarArea(point);
        }

        // Disable host resizing here without swallowing remote client input.
        // The former independent upper-right grip has been removed.
        if (form.IsTopRightControlArea(point))
            return false;
        if (form.IsToolbarArea(point))
        {
            // Do not let nested native windows turn inert toolbar
            // padding into a non-client drag/capture gesture.
            if (m.Msg == Interop.WM_NCLBUTTONDOWN)
            {
                form.EndRemoteWindowResize();
                return true;
            }
            return false;
        }
        if (form.IsToolbarDragPoint(point))
            return false;
        var hit = form.GetRemoteResizeHitTest(point);
        if (hit == Interop.HTCLIENT)
            return false;

        if (m.Msg == Interop.WM_MOUSEMOVE)
        {
            if (Control.MouseButtons != MouseButtons.None)
                return false;
            Cursor.Current = hit switch
            {
                Interop.HTLEFT or Interop.HTRIGHT => Cursors.SizeWE,
                Interop.HTTOP or Interop.HTBOTTOM => Cursors.SizeNS,
                Interop.HTTOPLEFT or Interop.HTBOTTOMRIGHT => Cursors.SizeNWSE,
                _ => Cursors.SizeNESW,
            };
            return true;
        }

        form.BeginRemoteWindowResize(hit, point);
        return true;
    }

    public void Dispose() => Application.RemoveMessageFilter(this);
}
