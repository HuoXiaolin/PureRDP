using PureRDP.Configuration;
using PureRDP.Configuration.Enums;
using PureRDP.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PureRDP.UI;

internal static class ShortcutBinding
{

    private static uint NativeModifiers(Keys key)
    {
        uint modifiers = 0;
        if ((key & Keys.Alt) != 0) modifiers |= 1;
        if ((key & Keys.Control) != 0) modifiers |= 2;
        if ((key & Keys.Shift) != 0) modifiers |= 4;
        if ((key & WindowsModifier) != 0) modifiers |= 8;
        return modifiers;
    }

    internal static bool IsAvailable(Keys key)
    {
        uint modifiers = NativeModifiers(key) | 0x4000;
        if (!RegisterHotKey(IntPtr.Zero, 0x5245, modifiers, (uint)(key & Keys.KeyCode))) return false;
        UnregisterHotKey(IntPtr.Zero, 0x5245);
        return true;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
    internal const Keys WindowsModifier = (Keys)0x00800000;
    internal static Keys Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Keys.None;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return Keys.None;

        var modifiers = Keys.None;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            modifiers |= parts[index].ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => Keys.Control,
                "ALT" => Keys.Alt,
                "SHIFT" => Keys.Shift,
                "WIN" or "WINDOWS" => WindowsModifier,
                _ => Keys.None,
            };
        }

        var keyName = parts[^1];
        if (keyName.Equals("DEL", StringComparison.OrdinalIgnoreCase)) keyName = "Delete";
        if (keyName.Equals("ESC", StringComparison.OrdinalIgnoreCase)) keyName = "Escape";
        if (keyName.Equals("RETURN", StringComparison.OrdinalIgnoreCase)) keyName = "Enter";
        if (!Enum.TryParse<Keys>(keyName, true, out var key) || IsModifierKey(key))
            return Keys.None;

        return Normalize(modifiers | (key & Keys.KeyCode));
    }

    internal static string Format(Keys shortcut)
    {
        shortcut = Normalize(shortcut);
        if (shortcut == Keys.None)
            return "未设置";

        var names = new List<string>();
        var modifiers = shortcut & Keys.Modifiers;
        if ((modifiers & Keys.Control) != Keys.None) names.Add("Ctrl");
        if ((modifiers & Keys.Alt) != Keys.None) names.Add("Alt");
        if ((modifiers & Keys.Shift) != Keys.None) names.Add("Shift");
        if ((shortcut & WindowsModifier) != Keys.None) names.Add("Win");

        var key = shortcut & Keys.KeyCode;
        names.Add(key switch
        {
            Keys.Return => "Enter",
            Keys.Prior => "PageUp",
            Keys.Next => "PageDown",
            Keys.Back => "Backspace",
            Keys.Escape => "Esc",
            Keys.Space => "Space",
            _ => key.ToString(),
        });
        return string.Join("+", names);
    }

    internal static bool Matches(string? configured, Keys pressed)
    {
        var expected = Parse(configured);
        return expected != Keys.None && Normalize(pressed) == expected;
    }

    private static Keys Normalize(Keys value)
        => (value & Keys.KeyCode) | (value & Keys.Modifiers);

    private static bool IsModifierKey(Keys key)
        => key is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey or
            Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey or
            Keys.Menu or Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin;
}

internal sealed class ShortcutCaptureBox : TextBox
{
    private Keys _shortcut;

    internal string ShortcutValue => _shortcut == Keys.None ? string.Empty : ShortcutBinding.Format(_shortcut);

    public ShortcutCaptureBox()
    {
        ReadOnly = true;
        ShortcutsEnabled = false;
        Width = 230;
        Height = 28;
        TabStop = true;
        Cursor = Cursors.Hand;
        TextAlign = HorizontalAlignment.Center;
        PlaceholderText = "点击后按下快捷键";
    }

    internal void LoadShortcut(string? value)
    {
        _shortcut = ShortcutBinding.Parse(value);
        Text = _shortcut == Keys.None ? string.Empty : ShortcutBinding.Format(_shortcut);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var keyData = e.KeyData;
        if ((GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0)
            keyData |= ShortcutBinding.WindowsModifier;
        if (e.KeyCode is Keys.Escape or Keys.Back)
        {
            _shortcut = Keys.None;
            Text = string.Empty;
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (ShortcutBinding.Parse(ShortcutBinding.Format(keyData)) == Keys.None ||
            (keyData & Keys.Modifiers) == Keys.None)
            return;

        _shortcut = keyData;
        Text = ShortcutBinding.Format(_shortcut);
        SelectionStart = TextLength;
        e.Handled = true;
        e.SuppressKeyPress = true;
        base.OnKeyDown(e);
    }
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}

internal sealed class ShortcutSettingsPage : SettingsPage
{
    private readonly Timer _conflictTimer = new() { Interval = 1000 };
    private readonly ShortcutCaptureBox _minimizeWindow = new();
    private readonly ShortcutCaptureBox _maximizeWindow = new();
    private readonly ShortcutCaptureBox _showConsole = new();
    private readonly ShortcutCaptureBox _toggleTrueFullscreen = new();
    private readonly ShortcutCaptureBox _cycleWindow = new();
    private readonly ShortcutCaptureBox _closeWindow = new();
    private readonly ComboBox _effectMode = new ModernComboBox() { DropDownStyle = ComboBoxStyle.DropDownList };

    public override string Title => "快捷键";

    public ShortcutSettingsPage(GlobalShortcutPreferences preferences)
    {
        BackColor = UiDesignTokens.Surface;
        var rows = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 11,
            AutoSize = true,
        };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        var title = new Label
        {
            Text = Title,
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold),
            ForeColor = UiDesignTokens.TextPrimary,
        };
        rows.Controls.Add(title, 0, 0);
        rows.SetColumnSpan(title, 2);

        var tip = new Label
        {
            Text = "点击下方输入框捕捉 PureRDP 组合键，至少包含 Ctrl、Alt、Shift 或 Win。自动检测冲突，红色项不生效；普通 Windows/RDP 按键仍按设备设置发送到本地或远端。",
            Dock = DockStyle.Fill,
            ForeColor = UiDesignTokens.TextSecondary,
            AutoEllipsis = true,
        };
        rows.Controls.Add(tip, 0, 1);
        rows.SetColumnSpan(tip, 2);

        var pureRdpTitle = new Label
        {
            Text = "PureRDP 快捷键",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            ForeColor = UiDesignTokens.TextPrimary,
        };
        rows.Controls.Add(pureRdpTitle, 0, 2);
        rows.SetColumnSpan(pureRdpTitle, 2);

        AddShortcutRow(rows, 3, "置前/最小化连接：", _minimizeWindow);
        AddShortcutRow(rows, 4, "最大化/还原窗口：", _maximizeWindow);
        AddShortcutRow(rows, 5, "打开控制台：", _showConsole);
        AddShortcutRow(rows, 6, "真全屏：", _toggleTrueFullscreen);
        AddShortcutRow(rows, 7, "循环切换连接窗口：", _cycleWindow);
        AddShortcutRow(rows, 8, "关闭连接窗口：", _closeWindow);
        var effectRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
        };
        effectRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        effectRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        effectRow.Controls.Add(new Label
        {
            Text = "PureRDP 快捷键生效逻辑：",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiDesignTokens.TextSecondary,
        }, 0, 0);
        _effectMode.Items.Add(new EffectModeItem(GlobalShortcutEffectMode.SyncConnectionWindow, "同步连接窗口"));
        _effectMode.Items.Add(new EffectModeItem(GlobalShortcutEffectMode.AlwaysLocal, "始终本地生效"));
        _effectMode.Dock = DockStyle.Left;
        _effectMode.Width = 230;
        effectRow.Controls.Add(_effectMode, 1, 0);
        rows.Controls.Add(effectRow, 0, 9);
        rows.SetColumnSpan(effectRow, 2);

        var otherKeys = new Label
        {
            Text = "其他快捷键：不属于上面的 PureRDP 功能，不受“始终本地生效”影响，继续遵循当前设备的键盘设置。",
            Dock = DockStyle.Fill,
            ForeColor = UiDesignTokens.TextSecondary,
            AutoEllipsis = true,
        };
        rows.Controls.Add(otherKeys, 0, 10);
        rows.SetColumnSpan(otherKeys, 2);
        Controls.Add(rows);

        LoadFrom(new GlobalSettings { Shortcuts = preferences.Clone() });
        foreach (var box in CaptureBoxes)
            box.TextChanged += (_, _) => ValidateShortcuts(out _);
        _conflictTimer.Tick += (_, _) => { if (Visible) ValidateShortcuts(out _); };
        VisibleChanged += (_, _) => { if (Visible) ValidateShortcuts(out _); };
        _conflictTimer.Start();
    }

    private ShortcutCaptureBox[] CaptureBoxes => new[] { _minimizeWindow, _maximizeWindow,
        _showConsole, _toggleTrueFullscreen, _cycleWindow, _closeWindow };

    protected override void Dispose(bool disposing)
    {
        if (disposing) _conflictTimer.Dispose();
        base.Dispose(disposing);
    }

    private static void AddShortcutRow(TableLayoutPanel rows, int row, string label, ShortcutCaptureBox capture)
    {
        rows.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiDesignTokens.TextSecondary,
        }, 0, row);
        var host = new Panel { Dock = DockStyle.Fill };
        capture.Location = new Point(0, 7);
        capture.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        host.Controls.Add(capture);
        rows.Controls.Add(host, 1, row);
    }

    public override void ApplyTo(GlobalSettings settings)
    {
        settings.Shortcuts.MinimizeConnectionWindow = _minimizeWindow.ShortcutValue;
        settings.Shortcuts.MaximizeConnectionWindow = _maximizeWindow.ShortcutValue;
        settings.Shortcuts.ShowControlConsole = _showConsole.ShortcutValue;
        settings.Shortcuts.ToggleTrueFullscreen = _toggleTrueFullscreen.ShortcutValue;
        settings.Shortcuts.CycleConnectionWindow = _cycleWindow.ShortcutValue;
        settings.Shortcuts.CloseConnectionWindow = _closeWindow.ShortcutValue;
        settings.Shortcuts.EffectMode = _effectMode.SelectedItem is EffectModeItem item
            ? item.Value : GlobalShortcutEffectMode.SyncConnectionWindow;
    }

    internal bool ValidateShortcuts(out string error)
    {
        var rows = new (string Name, ShortcutCaptureBox Box)[]
        {
            ("置前/最小化连接", _minimizeWindow), ("最大化/还原窗口", _maximizeWindow),
            ("打开控制台", _showConsole), ("真全屏", _toggleTrueFullscreen),
            ("循环切换连接窗口", _cycleWindow),
            ("关闭连接窗口", _closeWindow),
        };
        var counts = new Dictionary<Keys, int>();
        foreach (var (_, box) in rows)
        {
            var key = ShortcutBinding.Parse(box.ShortcutValue);
            if (key != Keys.None) counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        error = string.Empty;
        foreach (var (name, box) in rows)
        {
            var key = ShortcutBinding.Parse(box.ShortcutValue);
            box.ForeColor = UiDesignTokens.TextPrimary;
            if (key == Keys.None) continue;
            if (counts[key] > 1)
            {
                box.ForeColor = UiDesignTokens.Danger;
                error = $"快捷键冲突：{box.ShortcutValue} 被多个功能重复使用，红色项不生效。";
                continue;
            }
            if (!ShortcutBinding.IsAvailable(key))
            {
                error = $"“{name}”的 {box.ShortcutValue} 被系统或其他程序占用，或不支持注册，请更换。";
                box.ForeColor = UiDesignTokens.Danger;
                continue;
            }
        }
        return error.Length == 0;
    }
    public override void LoadFrom(GlobalSettings settings)
    {
        _minimizeWindow.LoadShortcut(settings.Shortcuts.MinimizeConnectionWindow);
        _maximizeWindow.LoadShortcut(settings.Shortcuts.MaximizeConnectionWindow);
        _showConsole.LoadShortcut(settings.Shortcuts.ShowControlConsole);
        _toggleTrueFullscreen.LoadShortcut(settings.Shortcuts.ToggleTrueFullscreen);
        _cycleWindow.LoadShortcut(settings.Shortcuts.CycleConnectionWindow);
        _closeWindow.LoadShortcut(settings.Shortcuts.CloseConnectionWindow);
        _effectMode.SelectedIndex = settings.Shortcuts.EffectMode == GlobalShortcutEffectMode.AlwaysLocal ? 1 : 0;
    }

    private sealed class EffectModeItem(GlobalShortcutEffectMode value, string display)
    {
        public GlobalShortcutEffectMode Value { get; } = value;
        public override string ToString() => display;
    }
}

internal sealed class GlobalShortcutMessageFilter : IMessageFilter, IDisposable
{
    private readonly FormRdpClient managementWindow;
    private readonly HookProc _callback;
    private IntPtr _hook;
    private readonly FocusEventProc _focusCallback;
    private IntPtr _focusHook;
    private readonly HashSet<Keys> _consumed = new();
    private readonly HashSet<int> _modifierKeysDown = new();
    private readonly Timer _availabilityTimer = new() { Interval = 1000 };
    private bool _disposed;

    private bool _remoteInputRefreshQueued;

    public GlobalShortcutMessageFilter(FormRdpClient window)
    {
        managementWindow = window;
        _callback = KeyboardCallback;
        _focusCallback = RemoteFocusChanged;
        managementWindow.RefreshShortcutAvailability();
        _availabilityTimer.Tick += (_, _) => managementWindow.RefreshShortcutAvailability();
        _availabilityTimer.Start();
        // Swallowed key-downs need not appear in GetAsyncKeyState. Keep the
        // repeat latch until the hook sees key-up, not an asynchronous poll.
        RefreshHook();
        // ActiveX can change focus inside its native message loop without
        // activating the form or delivering a WinForms mouse message.
        // Observe only this process, asynchronously, after that handoff.
        _focusHook = SetWinEventHook(0x8005, 0x8005, IntPtr.Zero, _focusCallback,
            (uint)Environment.ProcessId, 0, 0);
        if (_focusHook == IntPtr.Zero)
            ConnectionLogService.WriteDiagnostic($"shortcut focus-hook failed error={Marshal.GetLastWin32Error()}");
    }

    private void RemoteFocusChanged(IntPtr hook, uint eventType, IntPtr window,
        int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (_disposed || eventType != 0x8005 || !managementWindow.IsRemoteInputWindow(window))
            return;
        if (!_remoteInputRefreshQueued)
            ConnectionLogService.WriteDiagnostic("shortcut source=remote-focus refresh=queued");
        QueueRemoteInputHookRefresh();
    }

    private delegate void FocusEventProc(IntPtr hook, uint eventType, IntPtr window,
        int objectId, int childId, uint eventThread, uint eventTime);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
        FocusEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    internal void RefreshHook(bool preserveKeyState = false)
    {
        if (_disposed) return;
        // A mode/focus change can recreate the hook while a shortcut key is
        // still physically held.  Do not carry the old hook's key latch into
        // the new hook: that made the next press look like an auto-repeat and
        // silently swallowed the first invocation.
        if (!preserveKeyState)
        {
            _consumed.Clear();
            _modifierKeysDown.Clear();
        }
        // RDP installs its own hook on focus/mode changes. Rejoin ahead of it.
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            ConnectionLogService.WriteDiagnostic($"shortcut keyboard-hook failed error={Marshal.GetLastWin32Error()}");
    }


    private IntPtr KeyboardCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && !_disposed)
        {
            var key = (Keys)Marshal.ReadInt32(data);
            var msg = message.ToInt32();
            var vk = (int)key;
            TrackModifierKey(vk, msg);
            if (msg is 0x0101 or 0x0105)
            {
                if (_consumed.Remove(key)) return (IntPtr)1;
            }
            else if (msg is 0x0100 or 0x0104)
            {
                // Swallowed down events do not update GetAsyncKeyState.
                // Keep the latch until key-up so repeats cannot undo minimize.
                if (_consumed.Contains(key)) return (IntPtr)1;
                var modifiers = Keys.None;
                if (ModifierDown(0x11, 0xA2, 0xA3)) modifiers |= Keys.Control;
                if (ModifierDown(0x10, 0xA0, 0xA1)) modifiers |= Keys.Shift;
                // LLKHF_ALTDOWN belongs to this event, unlike the asynchronous
                // state which can lag behind a fast Alt chord.
                if (ModifierDown(0x12, 0xA4, 0xA5) || (Marshal.ReadInt32(data, 8) & 0x20) != 0) modifiers |= Keys.Alt;
                if (IsAsyncKeyDown(0x5B) || IsAsyncKeyDown(0x5C)) modifiers |= ShortcutBinding.WindowsModifier;
                try
                {
                    if (managementWindow.TryHandleGlobalShortcut(key | modifiers, queueOnly: true))
                    {
                        ConnectionLogService.WriteDiagnostic($"shortcut source=hook key={ShortcutBinding.Format(key | modifiers)}");
                        _consumed.Add(key);
                        return (IntPtr)1;
                    }
                }
                catch (InvalidOperationException) { }
            }
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    private static bool IsAsyncKeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    // Low-level hook events precede the asynchronous keyboard-state update.
    // Remember modifier down/up events so a fast first chord is not missed.
    private bool ModifierDown(int key, int left, int right)
        => _modifierKeysDown.Contains(key) || _modifierKeysDown.Contains(left) ||
           _modifierKeysDown.Contains(right) || IsAsyncKeyDown(key);

    private void TrackModifierKey(int key, int message)
    {
        if (key is not (0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C))
            return;
        if (message is 0x0100 or 0x0104)
            _modifierKeysDown.Add(key);
        else if (message is 0x0101 or 0x0105)
        {
            _modifierKeysDown.Remove(key);
            _consumed.Clear();
        }
    }
    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    public bool PreFilterMessage(ref Message message)
    {
        // Native RDP child focus can change without activating the host form.
        // Run after dispatch so the client's mouse/focus handling has finished
        // installing its keyboard hook. Never swallow the remote mouse input.
        if (!_disposed && message.Msg is 0x0201 or 0x0202 or 0x0203 or
            0x0204 or 0x0205 or 0x0206 or 0x0207 or 0x0208 or 0x0209 &&
            managementWindow.IsRemoteInputWindow(message.HWnd))
        {
            QueueRemoteInputHookRefresh();
            return false;
        }

        if (_disposed || message.Msg is not (0x0100 or 0x0104 or 0x0101 or 0x0105))
            return false;

        var key = (Keys)(message.WParam.ToInt32() & (int)Keys.KeyCode);
        if (key == Keys.None)
            return false;

        var modifierKey = (int)key;
        // Queue messages use generic Ctrl/Alt/Shift VKs while the hook uses
        // left/right VKs. Normalize them so both paths share release state.
        var extended = (message.LParam.ToInt64() & (1L << 24)) != 0;
        if (key == Keys.ControlKey) modifierKey = extended ? 0xA3 : 0xA2;
        else if (key == Keys.Menu) modifierKey = extended ? 0xA5 : 0xA4;
        else if (key == Keys.ShiftKey)
            modifierKey = ((message.LParam.ToInt64() >> 16) & 0xFF) == 0x36 ? 0xA1 : 0xA0;
        TrackModifierKey(modifierKey, message.Msg);

        if (message.Msg is 0x0101 or 0x0105)
            return _consumed.Remove(key);
        if (_consumed.Contains(key))
            return true;

        if (modifierKey is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C)
        {
            if (managementWindow.IsRemoteInputWindow(message.HWnd))
                QueueRemoteInputHookRefresh();
            return false;
        }
        if (!managementWindow.IsConfiguredShortcutKey(key))
            return false;

        // A nonzero hook handle does not prove that the callback still sees
        // input: native RDP can precede it, and Windows can remove a timed-out
        // hook silently. Handle a delivered chord now, not on the next press.
        var modifiers = Control.ModifierKeys;
        if (ModifierDown(0x11, 0xA2, 0xA3) || IsKeyDown(0x11)) modifiers |= Keys.Control;
        if (ModifierDown(0x10, 0xA0, 0xA1) || IsKeyDown(0x10)) modifiers |= Keys.Shift;
        if (ModifierDown(0x12, 0xA4, 0xA5) || IsKeyDown(0x12) || (message.Msg == 0x0104 && (message.LParam.ToInt64() & (1L << 29)) != 0))
            modifiers |= Keys.Alt;
        if (ModifierDown(0x5B, 0x5B, 0x5C)) modifiers |= ShortcutBinding.WindowsModifier;


        if (!managementWindow.TryHandleGlobalShortcut(key | modifiers, queueOnly: true))
            return false;
        _consumed.Add(key);
        ConnectionLogService.WriteDiagnostic($"shortcut source=message key={ShortcutBinding.Format(key | modifiers)}");
        if (managementWindow.IsRemoteInputWindow(message.HWnd))
            QueueRemoteInputHookRefresh();
        return true;
    }

    internal void QueueRemoteInputHookRefresh()
    {
        if (_remoteInputRefreshQueued || !managementWindow.IsHandleCreated || managementWindow.IsDisposed)
            return;

        _remoteInputRefreshQueued = true;
        try
        {
            managementWindow.BeginInvoke(new Action(() =>
            {
                _remoteInputRefreshQueued = false;
                if (!_disposed && !managementWindow.IsDisposed)
                    RefreshHook(preserveKeyState: true);
            }));
        }
        catch (InvalidOperationException)
        {
            _remoteInputRefreshQueued = false;
        }
    }

    private static bool IsKeyDown(int virtualKey)
        => (GetKeyState(virtualKey) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_focusHook != IntPtr.Zero)
        {
            UnhookWinEvent(_focusHook);
            _focusHook = IntPtr.Zero;
        }
        _availabilityTimer.Dispose();
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _consumed.Clear();
        Application.RemoveMessageFilter(this);
    }
}
