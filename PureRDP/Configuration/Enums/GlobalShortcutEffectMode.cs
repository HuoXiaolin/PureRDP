namespace PureRDP.Configuration.Enums;

/// <summary>
/// Controls whether PureRDP's own global shortcuts follow the foreground
/// connection window's Windows-key handling mode.
/// </summary>
internal enum GlobalShortcutEffectMode
{
    SyncConnectionWindow = 0,
    AlwaysLocal = 1,
}
