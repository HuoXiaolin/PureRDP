using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using PureRDP.UI;

internal static class Program
{
    private static readonly Assembly App = typeof(FormSettings).Assembly;
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static Type Type(string name) => App.GetType("PureRDP." + name)!;
    private static object New(string name, params object?[] args) => Activator.CreateInstance(Type(name), Members, null, args, null)!;
    private static object Field(object target, string name) => target.GetType().GetField(name, Members)!.GetValue(target)!;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Members)!.Invoke(target, args);
    private static int _checks;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); _checks++; }
    private static object Device(string title, string address)
    {
        var device = New("Configuration.ConnectionSettings");
        device.GetType().GetProperty("DisplayName")!.SetValue(device, title);
        device.GetType().GetProperty("Hostname")!.SetValue(device, address);
        return device;
    }
    private static Control[] Cards(Control panel) => ((Control)Field(panel, "_itemsPanel")).Controls.Cast<Control>().ToArray();
    private static string Host(object card) => (string)card.GetType().GetProperty("Settings")!.GetValue(card)!.GetType().GetProperty("Hostname")!.GetValue(card.GetType().GetProperty("Settings")!.GetValue(card))!;
    private static Button Star(object card) => (Button)Field(card, "_pinButton");
    private static bool Pinned(object card) => (bool)card.GetType().GetProperty("IsPinned", Members)!.GetValue(card)!;
    private static int GoldPixels(Control button)
    {
        using var image = new Bitmap(button.Width, button.Height);
        button.DrawToBitmap(image, button.ClientRectangle);
        var count = 0;
        for (var x = 0; x < image.Width; x++) for (var y = 0; y < image.Height; y++)
        { var pixel = image.GetPixel(x, y); if (pixel.R > 190 && pixel.G > 125 && pixel.G < 225 && pixel.B < 120) count++; }
        return count;
    }
    [STAThread]
    private static void Main()
    {
        var layoutType = Type("Services.ConnectionLibraryLayoutSettings");
        var json = "{\"viewMode\":\"Cards\",\"order\":[],\"autoTileSessions\":true}";
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        Func<object> read = () => JsonSerializer.Deserialize(json, layoutType, options)!;
        var reader = Expression.Lambda(typeof(Func<>).MakeGenericType(layoutType),
            Expression.Convert(Expression.Invoke(Expression.Constant(read)), layoutType)).Compile();
        Func<object, bool> write = value => { json = JsonSerializer.Serialize(value, layoutType, options); return true; };
        var devices = new[] { Device("示例一", "192.0.2.1"), Device("示例二", "192.0.2.2"), Device("示例三", "192.0.2.3") };
        using var host = new Form { ClientSize = new Size(1160, 800), StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false };
        using var panel = (Control)New("UI.ConnectionLibraryPanel", reader, write);
        host.Controls.Add(panel); host.Show(); Application.DoEvents();
        void Fill(Control target) { var connections = (IList)Field(target, "_connections"); connections.Clear(); foreach (var device in devices) connections.Add(device); Call(target, "RenderConnections"); }
        Fill(panel);
        var original = Cards(panel);
        var viewportMode = (ComboBox)Field(panel, "_viewModeComboBox");
        foreach (var view in new[] { 0, 1, 2 })
        foreach (var viewport in new[] { 420, 900, 1400 })
        {
            viewportMode.SelectedIndex = view;
            host.ClientSize = new Size(viewport, 800);
            Application.DoEvents();
            Call(panel, "UpdateCardSizes");
            Application.DoEvents();
            var flow = (Control)Field(panel, "_itemsPanel");
            var rows = Cards(panel);
            var availableWidth = flow.ClientSize.Width - flow.Padding.Horizontal;
            var columnCount = view == 0 ? Math.Clamp(availableWidth / 360, 1, 3) : 1;
            var rowIndex = 0;
            foreach (var row in rows)
            {
                var expectedWidth = Math.Max(view == 2 ? 560 : 280,
                    availableWidth / columnCount + (rowIndex++ % columnCount < availableWidth % columnCount ? 1 : 0) - row.Margin.Horizontal);
                Check(row.Width == expectedWidth, "Device must fill its responsive grid cell or full row.");
                if (view == 2)
                {
                    if (availableWidth - row.Margin.Horizontal >= 560)
                        Check(row.Width + row.Margin.Horizontal == availableWidth,
                            "List must fit the viewport without forcing a 1200px row.");
                    var fields = new[] { "_titleLabel", "_hostLabel", "_metaLabel", "_hintLabel", "_qualityLabel" }
                        .Select(name => (Control)Field(row, name)).ToArray();
                    Check(fields.Select(field => field.Top).Distinct().Count() == 1 && row.Height == 48,
                        "List view must retain a compact single-line field baseline.");
                    Check(fields.All(field => field.Height == 28), "List fields must not wrap onto a second row.");
                }
            }
            Check(view == 0 ? rows.GroupBy(row => row.Top).All(group => group.Count() <= columnCount) :
                rows.Select(row => row.Left).Distinct().Count() == 1 &&
                rows.Zip(rows.Skip(1), (a, b) => b.Top >= a.Bottom).All(value => value),
                $"Each device must occupy its own complete row: view={view}, viewport={viewport}, bounds={string.Join(';', rows.Select(row => row.Bounds))}");
            if (view == 0)
            {
                Check(rows.Take(columnCount).Select(row => row.Top).Distinct().Count() == 1,
                    "First card row must contain the expected responsive column count.");
                Check(rows[0].Left + rows.Take(columnCount).Sum(row => row.Width + row.Margin.Horizontal) - rows[0].Margin.Left
                    == flow.Padding.Left + availableWidth, "Card columns must use the entire available row width.");
            }
            Directory.CreateDirectory(Path.Combine("outputs", "ui-review"));
            if (viewport == 1400)
            {
                using var screenshot = new Bitmap(host.Width, host.Height);
                host.DrawToBitmap(screenshot, host.ClientRectangle);
                screenshot.Save(Path.Combine("outputs", "ui-review", $"FullRowView-{view}.png"));
            }
        }
        host.ClientSize = new Size(1160, 800);
        viewportMode.SelectedIndex = 0;
        Application.DoEvents();
        Call(panel, "OnDpiChangedAfterParent", EventArgs.Empty);
        Call(panel, "OnDpiChangedAfterParent", EventArgs.Empty);
        Check((bool)Field(panel, "_dpiRefreshQueued"), "DPI notifications must queue a coalesced final layout pass.");
        Application.DoEvents();
        Check(!(bool)Field(panel, "_dpiRefreshQueued"), "Final DPI layout must complete after child controls finish scaling.");
        Check(Cards(panel).SequenceEqual(original), "DPI recovery must preserve card instances, pin order and previews.");
        var connects = 0;
        panel.GetType().GetEvent("ConnectRequested")!.AddEventHandler(panel, (Action<object>)(_ => connects++));
        Check(original.All(card => !Pinned(card)), "Legacy config without pinned list must stay compatible.");
        Check(GoldPixels(Star(original[1])) == 0, "Unpinned star must be an outline, not yellow.");
        Star(original[1]).PerformClick();
        Check(Host(Cards(panel)[0]) == "192.0.2.2", "Star click must immediately pin device to the top.");
        Check(Pinned(original[1]) && GoldPixels(Star(original[1])) > 50, "Pinned star must be visibly yellow.");
        Check(Star(original[1]).AccessibleName == "取消置顶", "Pinned state must be accessible.");
        Check(connects == 0, "Star click must not connect.");
        Check(Cards(panel).All(original.Contains), "Pinning must move existing controls instead of recreating previews.");
        using (var saved = JsonDocument.Parse(json)) {
            Check(saved.RootElement.GetProperty("pinned")[0].GetString() == "192.0.2.2", "Pin must be persisted.");
            Check(saved.RootElement.GetProperty("autoTileSessions").GetBoolean(), "Pin persistence must preserve other layout preferences.");
        }
        Star(original[2]).PerformClick();
        Check(Cards(panel).Select(Host).SequenceEqual(new[] { "192.0.2.3", "192.0.2.2", "192.0.2.1" }), "Most recently pinned device comes first.");
        Star(original[1]).PerformClick();
        Check(Cards(panel).Select(Host).SequenceEqual(new[] { "192.0.2.3", "192.0.2.1", "192.0.2.2" }), "Unpin returns device to normal manual order.");
        Check(!Pinned(original[1]) && GoldPixels(Star(original[1])) == 0, "Unpin clears the yellow state.");
        var mode = (ComboBox)Field(panel, "_viewModeComboBox");
        foreach (var index in new[] { 1, 2, 0 }) {
            mode.SelectedIndex = index;
            Check(Cards(panel)[0] == original[2] && Pinned(original[2]), "Pin must persist across view modes.");
            foreach (var card in Cards(panel)) Check(!Star(card).Bounds.IntersectsWith(((Control)Field(card, "_avatarPanel")).Bounds), "Star must not overlap avatar.");
        }
        var search = (TextBox)Field(panel, "_searchBox"); search.Text = "示例二";
        Check(Cards(panel).Length == 1, "Search keeps normal filtering.");
        Star(Cards(panel)[0]).PerformClick(); search.Clear();
        Check(Cards(panel).Select(Host).SequenceEqual(new[] { "192.0.2.2", "192.0.2.3", "192.0.2.1" }), "Pinning a search result retains hidden devices and pin ordering.");
        using var reloaded = (Control)New("UI.ConnectionLibraryPanel", reader, write);
        host.Controls.Clear(); host.Controls.Add(reloaded); Application.DoEvents(); Fill(reloaded);
        Check(Cards(reloaded).Select(Host).SequenceEqual(new[] { "192.0.2.2", "192.0.2.3", "192.0.2.1" }), "Saved pins survive reopening.");
        var replacement = Device("示例二", "192.0.2.22");
        Check((bool)Call(reloaded, "UpdateConnection", replacement, "192.0.2.2")!, "Renamed device updates normally.");
        using (var saved = JsonDocument.Parse(json)) Check(saved.RootElement.GetProperty("pinned")[0].GetString() == "192.0.2.22", "Editing an address migrates its pin.");
        var keyboardCard = Cards(reloaded).Last();
        Check(Star(keyboardCard).Focus(), "Star must be keyboard focusable.");
        Check((bool)Call(keyboardCard, "ProcessCmdKey", new Message(), Keys.Enter)!, "Enter on star must be handled.");
        Check(Pinned(keyboardCard), "Enter on focused star pins the device.");
        Check((bool)Call(keyboardCard, "ProcessCmdKey", new Message(), Keys.Space)!, "Space on star must be handled.");
        Check(!Pinned(keyboardCard), "Space on focused star unpins the device.");
        var items = (Control)Field(reloaded, "_itemsPanel");
        var beforeDrag = Cards(reloaded);
        void DragTo(Control dragged, Control target, int offsetY)
        {
            reloaded.GetType().GetField("_draggedCard", Members)!.SetValue(reloaded, dragged);
            var point = items.PointToScreen(new Point(target.Left + target.Width / 2, target.Top + offsetY));
            Call(reloaded, "MoveDraggedCard", new DragEventArgs(new DataObject(), 0, point.X, point.Y, DragDropEffects.Move, DragDropEffects.Move));
        }
        DragTo(beforeDrag[0], beforeDrag[2], 10);
        Check(Cards(reloaded).SequenceEqual(beforeDrag), "Pinned device cannot be dragged into normal group.");
        DragTo(beforeDrag[2], beforeDrag[0], 10);
        Check(Cards(reloaded).SequenceEqual(beforeDrag), "Normal device cannot be dragged into pinned group.");
        DragTo(beforeDrag[0], beforeDrag[1], beforeDrag[1].Height - 10);
        Check(Cards(reloaded).Take(2).All(Pinned) && !Pinned(Cards(reloaded)[2]), "Dropping after last pinned device must not cross group boundary.");
        Check(Cards(reloaded)[0] == beforeDrag[1], "Pinned devices can still be reordered within their group.");
        Call(reloaded, "RebuildOrderFromCards"); Call(reloaded, "SaveOrder");
        using (var saved = JsonDocument.Parse(json)) Check(saved.RootElement.GetProperty("pinned")[0].GetString() == Host(beforeDrag[1]), "Manual pinned ordering must be persisted.");
        var closedPreviewCard = Cards(reloaded)[0];
        closedPreviewCard.GetType().GetProperty("PreviewEnabled", Members)!.SetValue(closedPreviewCard, true);
        closedPreviewCard.GetType().GetProperty("PreviewOpen", Members)!.SetValue(closedPreviewCard, false);
        Check(((Control)Field(closedPreviewCard, "_titleLabel")).Width >= closedPreviewCard.Width - 226, "Closed preview must leave room for the title beside the star.");
        var reloadedSearch = (TextBox)Field(reloaded, "_searchBox"); reloadedSearch.Text = "示例一";
        Call(reloaded, "UpdateConnection", Device("示例二", "192.0.2.222"), "192.0.2.22");
        using (var saved = JsonDocument.Parse(json)) Check(saved.RootElement.GetProperty("pinned").EnumerateArray().Any(item => item.GetString() == "192.0.2.222"), "Renaming a filtered-out device must also persist its pin.");
        reloadedSearch.Clear();
        foreach (var card in Cards(reloaded).Where(Pinned).ToArray()) Star(card).PerformClick();
        var dragMode = (ComboBox)Field(reloaded, "_viewModeComboBox");
        foreach (var index in new[] { 0, 1, 2 })
        {
            dragMode.SelectedIndex = index;
            Application.DoEvents();
            void MoveThroughEvents(Control dragged, Control target, bool dropOnly = false)
            {
                var before = Cards(reloaded);
                var expected = before.ToList();
                var targetIndex = expected.IndexOf(target);
                expected.Remove(dragged); expected.Insert(targetIndex, dragged);
                reloaded.GetType().GetField("_draggedCard", Members)!.SetValue(reloaded, dragged);
                var point = items.PointToScreen(new Point(target.Left + target.Width / 2, target.Bottom - 12));
                var args = new DragEventArgs(new DataObject(dragged), 0, point.X, point.Y, DragDropEffects.Move, DragDropEffects.None);
                Call(reloaded, "ItemsPanel_DragEnter", items, args);
                Check(args.Effect == DragDropEffects.Move, "Drag must be accepted in view " + index);
                if (!dropOnly)
                {
                    Call(reloaded, "ItemsPanel_DragOver", items, args);
                    Check(Cards(reloaded).SequenceEqual(expected), "Dragging onto lower half must reorder in view " + index);
                    Call(reloaded, "ItemsPanel_DragOver", items, args);
                    Check(Cards(reloaded).SequenceEqual(expected), "Repeated hover must not undo a reorder in view " + index);
                }
                Call(reloaded, "ItemsPanel_DragDrop", items, args);
                Check(Cards(reloaded).SequenceEqual(expected), "Drop must use the final pointer position in view " + index);
                Check(Cards(reloaded).All(before.Contains), "Dragging must reuse cards and previews.");
                using var saved = JsonDocument.Parse(json);
                Check(saved.RootElement.GetProperty("order").EnumerateArray().Select(item => item.GetString()).SequenceEqual(Cards(reloaded).Select(Host)), "Drop must persist the displayed order.");
                Call(reloaded, "RenderConnections");
                Check(Cards(reloaded).Select(Host).SequenceEqual(expected.Select(Host)), "Render must retain the dragged order.");
            }
            var current = Cards(reloaded);
            MoveThroughEvents(current[1], current[0]);
            current = Cards(reloaded);
            MoveThroughEvents(current[0], current[^1]);
            current = Cards(reloaded);
            MoveThroughEvents(current[^1], current[0]);
            current = Cards(reloaded);
            MoveThroughEvents(current[^1], current[0], dropOnly: true);
        }
        Check(connects == 0, "Favorite and mode operations never request a connection.");
        Console.WriteLine($"PASS: {_checks} favorite checks; persistence uses in-memory JSON only, no user settings saved or RDP login.");
    }
}
