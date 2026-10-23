using PureRDP.Platform;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.ComponentModel;
using System.Security.Cryptography;

namespace PureRDP.Services;

/// <summary>
/// Service for managing RDP credentials stored in Windows Credential Manager
/// </summary>
internal static class RdpCredentialService
{
    private const string TERMSRV_PREFIX = "TERMSRV/";

    // Private, endpoint-specific entries do not overwrite mstsc/other clients'
    // host-wide credentials. Secrets never enter an RDP file or diagnostic log.
    private static string PrivateTarget(string hostname)
        => "PureRDP/TERMSRV/" + hostname.Trim().ToLowerInvariant();

    internal static void SaveConnectionPassword(string hostname, string userName, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        WriteCredential(PrivateTarget(hostname), userName, password, Interop.CredentialType.Generic);
    }

    internal static string? ReadConnectionPassword(string? hostname, string? userName)
    {
        if (string.IsNullOrWhiteSpace(hostname) || string.IsNullOrWhiteSpace(userName)) return null;
        IntPtr pointer = IntPtr.Zero;
        try
        {
            if (!Interop.CredRead(PrivateTarget(hostname), Interop.CredentialType.Generic, 0, out pointer) || pointer == IntPtr.Zero)
                return null;
            var credential = Marshal.PtrToStructure<Interop.CREDENTIAL>(pointer);
            var savedUser = Marshal.PtrToStringUni(credential.UserName);
            if (!string.Equals(savedUser, userName, StringComparison.OrdinalIgnoreCase) ||
                credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize % 2 != 0 ||
                credential.CredentialBlobSize > 2560) return null;
            return Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)credential.CredentialBlobSize / 2));
        }
        finally { if (pointer != IntPtr.Zero) Interop.CredFree(pointer); }
    }

    internal static void RemoveConnectionPassword(string hostname)
        => Interop.CredDelete(PrivateTarget(hostname), Interop.CredentialType.Generic, 0);

    /// <summary>
    /// Checks if saved credentials exist for an RDP connection
    /// </summary>
    /// <param name="hostname">The hostname or IP address. Can include port (e.g., "server.com:3389")</param>
    /// <returns>The saved username if credentials exist, null otherwise</returns>
    public static string? CredentialsExist(string? hostname)
    {
        var references = GetCredentialReferences(hostname);
        if (!string.IsNullOrWhiteSpace(hostname))
            references = references.Prepend((PrivateTarget(hostname), Interop.CredentialType.Generic));
        foreach (var (credentialTarget, credentialType) in references)
        {
            IntPtr credentialPtr = IntPtr.Zero;
            try
            {
                if (!Interop.CredRead(credentialTarget, credentialType, 0, out credentialPtr) ||
                    credentialPtr == IntPtr.Zero)
                    continue;
                var credential = Marshal.PtrToStructure<Interop.CREDENTIAL>(credentialPtr);
                var userName = credential.UserName == IntPtr.Zero
                    ? null
                    : Marshal.PtrToStringUni(credential.UserName);
                if (!string.IsNullOrWhiteSpace(userName))
                    return userName;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
            finally
            {
                if (credentialPtr != IntPtr.Zero)
                {
                    try { Interop.CredFree(credentialPtr); }
                    catch (Exception exception) when (exception is not OutOfMemoryException) { }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Saves credentials for an RDP connection to Windows Credential Manager
    /// </summary>
    /// <param name="hostname">The hostname or IP address. Can include port (e.g., "server.com:3389")</param>
    /// <param name="userName">The username. Can include domain in format "domain\username" or "username@domain"</param>
    /// <param name="password">The password to save</param>
    public static void SaveCredentials(string hostname, string userName, string? password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);

        WriteCredential(GetCredentialTarget(hostname), userName, password, Interop.CredentialType.DomainPassword);
        if (password != null) SaveConnectionPassword(hostname, userName, password);
    }

    private static void WriteCredential(string credentialTarget, string userName, string? password,
        Interop.CredentialType type)
    {
        var passwordBytes = Encoding.Unicode.GetBytes(password ?? string.Empty);
        var targetNamePtr = Marshal.StringToCoTaskMemUni(credentialTarget);
        var userNamePtr = Marshal.StringToCoTaskMemUni(userName);
        var credentialBlobPtr = Marshal.AllocCoTaskMem(passwordBytes.Length);
        Marshal.Copy(passwordBytes, 0, credentialBlobPtr, passwordBytes.Length);

        var credential = new Interop.CREDENTIAL
        {
            Flags = 0,
            Type = (int)type,
            TargetName = targetNamePtr,
            Comment = IntPtr.Zero,
            LastWritten = default,
            CredentialBlobSize = (uint)passwordBytes.Length,
            CredentialBlob = credentialBlobPtr,
            Persist = Interop.CRED_PERSIST_LOCAL_MACHINE,
            AttributeCount = 0,
            Attributes = IntPtr.Zero,
            TargetAlias = IntPtr.Zero,
            UserName = userNamePtr
        };

        var credentialPtr = IntPtr.Zero;
        try
        {
            credentialPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(Interop.CREDENTIAL)));
            Marshal.StructureToPtr(credential, credentialPtr, false);
            if (!Interop.CredWrite(credentialPtr, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法保存远程桌面凭据。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            if (credentialPtr != IntPtr.Zero)
                Marshal.FreeCoTaskMem(credentialPtr);
            if (targetNamePtr != IntPtr.Zero)
                Marshal.FreeCoTaskMem(targetNamePtr);
            if (userNamePtr != IntPtr.Zero)
                Marshal.FreeCoTaskMem(userNamePtr);
            if (credentialBlobPtr != IntPtr.Zero)
            {
                Marshal.Copy(passwordBytes, 0, credentialBlobPtr, passwordBytes.Length);
                Marshal.FreeCoTaskMem(credentialBlobPtr);
            }
        }
    }

    /// <summary>
    /// Updates the username of an existing RDP credential while preserving its
    /// encrypted password and other Credential Manager metadata.
    /// </summary>
    /// <remarks>
    /// Credential Manager keys RDP credentials by target, not by username. If
    /// a device profile changes its username, leaving the old username under
    /// the same TERMSRV target makes the native RDP control authenticate as
    /// the old account even though the profile was saved correctly.
    /// </remarks>
    public static bool UpdateSavedUsername(string? hostname, string? userName)
    {
        if (string.IsNullOrWhiteSpace(hostname) || string.IsNullOrWhiteSpace(userName))
            return false;

        var foundCredential = false;
        var updatedAllCredentials = true;
        foreach (var (credentialTarget, credentialType) in GetCredentialReferences(hostname))
        {
            var updated = TryUpdateSavedUsername(credentialTarget, credentialType, userName,
                out var credentialWasFound);
            // A failed update must not destroy a previously saved secret.
            foundCredential |= credentialWasFound;
            updatedAllCredentials &= updated;
        }

        return foundCredential && updatedAllCredentials;
    }

    private static bool TryUpdateSavedUsername(string credentialTarget,
        Interop.CredentialType credentialType, string userName, out bool credentialWasFound)
    {
        credentialWasFound = false;
        IntPtr credentialPtr = IntPtr.Zero;
        try
        {
            if (!Interop.CredRead(credentialTarget, credentialType, 0, out credentialPtr) ||
                credentialPtr == IntPtr.Zero)
                return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return true;
        }

        IntPtr targetNamePtr = IntPtr.Zero;
        IntPtr userNamePtr = IntPtr.Zero;
        IntPtr credentialBlobPtr = IntPtr.Zero;
        IntPtr updatedCredentialPtr = IntPtr.Zero;
        try
        {
            credentialWasFound = true;
            var existing = Marshal.PtrToStructure<Interop.CREDENTIAL>(credentialPtr);
            var existingUserName = existing.UserName == IntPtr.Zero
                ? null
                : Marshal.PtrToStringUni(existing.UserName);
            if (string.Equals(existingUserName, userName, StringComparison.Ordinal))
                return true;

            var credentialBlobSize = checked((int)existing.CredentialBlobSize);
            var credentialBlob = new byte[credentialBlobSize];
            if (credentialBlobSize > 0 && existing.CredentialBlob != IntPtr.Zero)
                Marshal.Copy(existing.CredentialBlob, credentialBlob, 0, credentialBlobSize);

            targetNamePtr = Marshal.StringToCoTaskMemUni(credentialTarget);
            userNamePtr = Marshal.StringToCoTaskMemUni(userName);
            credentialBlobPtr = Marshal.AllocCoTaskMem(Math.Max(1, credentialBlob.Length));
            if (credentialBlob.Length > 0)
                Marshal.Copy(credentialBlob, 0, credentialBlobPtr, credentialBlob.Length);

            var updated = existing;
            updated.TargetName = targetNamePtr;
            updated.UserName = userNamePtr;
            updated.CredentialBlob = credentialBlobPtr;
            updated.CredentialBlobSize = (uint)credentialBlob.Length;

            updatedCredentialPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<Interop.CREDENTIAL>());
            Marshal.StructureToPtr(updated, updatedCredentialPtr, false);
            return Interop.CredWrite(updatedCredentialPtr, 0);
        }
        catch (OverflowException)
        {
            return false;
        }
        finally
        {
            if (updatedCredentialPtr != IntPtr.Zero)
                Marshal.FreeCoTaskMem(updatedCredentialPtr);
            if (credentialBlobPtr != IntPtr.Zero)
                Marshal.FreeCoTaskMem(credentialBlobPtr);
            if (userNamePtr != IntPtr.Zero)
                Marshal.FreeCoTaskMem(userNamePtr);
            if (targetNamePtr != IntPtr.Zero)
                Marshal.FreeCoTaskMem(targetNamePtr);
            if (credentialPtr != IntPtr.Zero)
            {
                try { Interop.CredFree(credentialPtr); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
            }
        }
    }

    /// <summary>
    /// Removes saved credentials for an RDP connection
    /// </summary>
    /// <param name="hostname">The hostname or IP address. Can include port (e.g., "server.com:3389")</param>
    /// <returns>True if credentials were removed successfully, false otherwise</returns>
    public static void RemoveCredentials(string? hostname)
    {
        if (!string.IsNullOrWhiteSpace(hostname)) RemoveConnectionPassword(hostname);
        foreach (var (credentialTarget, credentialType) in GetCredentialReferences(hostname))
        {
            try { Interop.CredDelete(credentialTarget, credentialType, 0); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
    }

    private static IEnumerable<(string Target, Interop.CredentialType Type)> GetCredentialReferences(
        string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            yield break;

        var endpoint = hostname.Trim();
        var server = RemovePort(endpoint);
        foreach (var target in new[] { server, endpoint }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Support both entries created by this application and generic
            // TERMSRV entries created by Windows/mstsc.
            yield return ($"{TERMSRV_PREFIX}{target}", Interop.CredentialType.DomainPassword);
            yield return ($"{TERMSRV_PREFIX}{target}", Interop.CredentialType.Generic);
        }
    }

    private static string GetCredentialTarget(string hostname)
        => $"{TERMSRV_PREFIX}{RemovePort(hostname.Trim())}";

    private static string RemovePort(string hostname)
    {
        if (hostname.StartsWith("[", StringComparison.Ordinal))
        {
            var closingBracket = hostname.IndexOf(']');
            if (closingBracket > 0 &&
                (closingBracket == hostname.Length - 1 || hostname[closingBracket + 1] == ':'))
                return hostname[..(closingBracket + 1)];
        }

        var separator = hostname.LastIndexOf(':');
        if (separator > 0 && separator == hostname.IndexOf(':') && separator < hostname.Length - 1 &&
            hostname[(separator + 1)..].All(char.IsDigit))
            return hostname[..separator];

        return hostname;
    }
}
