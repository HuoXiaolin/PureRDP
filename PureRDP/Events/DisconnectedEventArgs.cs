using PureRDP.Models;
using System;

namespace PureRDP.Events;

/// <summary>
/// Provides data for the OnDisconnected event.
/// </summary>
public class DisconnectedEventArgs : EventArgs
{
    /// <summary>
    /// Gets the raw disconnect reason code.
    /// </summary>
    public DisconnectReasonCode ReasonCode { get; }

    /// <summary>
    /// Gets the raw extended disconnect reason code.
    /// </summary>
    public DisconnectReasonCodeExt ExtendedReasonCode { get; }

    /// <summary>
    /// Human-readable description of the disconnect reason
    /// </summary>
    public string? ErrorDescription { get; }

    /// <summary>
    /// <c>true</c> if initiated by a reconnect; otherwise, <c>false</c>.
    /// </summary>
    public bool IsReconnect { get; }

    /// <summary>
    /// Gets whether the session was in RDP fullscreen mode before it disconnected.
    /// </summary>
    public bool WasFullScreen { get; }

    /// <summary>
    /// Gets whether the session used all monitors before it disconnected.
    /// </summary>
    public bool WasMultiMonitor { get; }

    /// <summary>
    /// The server explicitly reported shutdown (ERRINFO_SERVER_SHUTDOWN).
    /// A reboot or an unreachable endpoint alone is not proof of shutdown.
    /// </summary>
    public bool IsRemoteShutdown => ExtendedReasonCode == DisconnectReasonCodeExt.ServerShutdown;

    /// <summary>
    /// Initializes a new instance of the <see cref="DisconnectedEventArgs"/> class.
    /// </summary>
    /// <param name="errorCode">The disconnect reason code.</param>
    /// <param name="extendedErrorCode">The extended disconnect reason code.</param>
    /// <param name="errorDescription">Human-readable description of the disconnect reason</param>
    /// <param name="isReconnect"><c>true</c> if initiated by a reconnect; otherwise, <c>false</c>.</param>
    /// <param name="wasFullScreen">Whether the session was fullscreen before disconnecting.</param>
    /// <param name="wasMultiMonitor">Whether the session used all monitors before disconnecting.</param>
    public DisconnectedEventArgs(DisconnectReasonCode errorCode, DisconnectReasonCodeExt extendedErrorCode,
        string? errorDescription, bool isReconnect, bool wasFullScreen = false, bool wasMultiMonitor = false)
    {
        ReasonCode = errorCode;
        ExtendedReasonCode = extendedErrorCode;
        ErrorDescription = errorDescription;
        IsReconnect = isReconnect;
        WasFullScreen = wasFullScreen;
        WasMultiMonitor = wasMultiMonitor;
    }

    /// <summary>
    /// Gets whether the disconnection was intentional (not an error).
    /// </summary>
    public bool IsIntentionalDisconnect =>
        ReasonCode
            is DisconnectReasonCode.LocalNotError
            or DisconnectReasonCode.RemoteByUser
            or DisconnectReasonCode.ByServer
            or DisconnectReasonCode.ConnectionCanceled
        &&
        ExtendedReasonCode
            is DisconnectReasonCodeExt.NoInfo
            or DisconnectReasonCodeExt.LogoffByUser
            or DisconnectReasonCodeExt.RpcInitiatedDisconnectByUser
            or DisconnectReasonCodeExt.ApiInitiatedDisconnect;
}
