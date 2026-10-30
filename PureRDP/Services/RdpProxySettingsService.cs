using PureRDP.Configuration;
using System;

namespace PureRDP.Services;

internal static class RdpProxySettingsService
{
    // A positive list prevents host identities, passwords, tickets, and future
    // vendor-specific options from entering the shared proxy template.
    private static readonly string[] Fields = {
        nameof(ConnectionSettings.EnableCredSsp), nameof(ConnectionSettings.AuthenticationLevel),
        nameof(ConnectionSettings.Width), nameof(ConnectionSettings.Height), nameof(ConnectionSettings.ScreenMode),
        nameof(ConnectionSettings.UseAllMonitors), nameof(ConnectionSettings.AutoMaximizeWindow),
        nameof(ConnectionSettings.AutoResize), nameof(ConnectionSettings.SmartSizing),
        nameof(ConnectionSettings.ScaleFactor), nameof(ConnectionSettings.ViewScaleFactor), nameof(ConnectionSettings.TileScaleFactor),
        nameof(ConnectionSettings.ColorDepth), nameof(ConnectionSettings.EnableCompression), nameof(ConnectionSettings.EnableBitmapPersistence),
        nameof(ConnectionSettings.KeyboardModeWindowed), nameof(ConnectionSettings.KeyboardModeWindowFullscreen), nameof(ConnectionSettings.KeyboardModeFullscreen),
        nameof(ConnectionSettings.AudioPlaybackMode), nameof(ConnectionSettings.RedirectAudioCapture), nameof(ConnectionSettings.RedirectDrives),
        nameof(ConnectionSettings.RedirectClipboard), nameof(ConnectionSettings.RedirectPrinters), nameof(ConnectionSettings.RedirectSmartCards),
        nameof(ConnectionSettings.RedirectCameras), nameof(ConnectionSettings.ControlBarHiddenWindowed), nameof(ConnectionSettings.ControlBarHiddenFullscreen),
    };

    internal static ConnectionSettings Sanitize(ConnectionSettings? source)
    {
        var result = new ConnectionSettings();
        Copy(source ?? new ConnectionSettings(), result, useDefaults: true);
        return result;
    }

    private static void Copy(ConnectionSettings source, ConnectionSettings target, bool useDefaults = false)
    {
        foreach (var name in Fields)
        {
            var property = typeof(ConnectionSettings).GetProperty(name)!;
            var value = property.GetValue(source);
            if (useDefaults && value == null) value = property.GetValue(ConnectionSettings.Default);
            property.SetValue(target, value);
        }
    }

    internal static void Apply(ConnectionSettings target, GlobalRdpProxyPreferences preferences)
    {
        if (preferences.UseCommonSettings)
        {
            Copy(Sanitize(preferences.Connection), target);
            target.PreserveImportedDesktop = false;
        }
    }
}
