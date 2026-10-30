using PureRDP.Configuration;
using System;
using System.Security.Cryptography;
using System.Text;

namespace PureRDP.Services;

internal static class RdpFilePasswordService
{
    internal static string? GetPassword(ConnectionSettings settings)
    {
        // A password explicitly entered for this connection takes precedence.
        if (settings.Password != null) return settings.Password;
        var encoded = settings.EncryptedPassword51;
        if (string.IsNullOrEmpty(encoded)) return null;
        byte[]? encrypted = null;
        byte[]? plaintext = null;
        try
        {
            if (encoded.Length > 131072 || encoded.Length % 2 != 0)
                throw new FormatException();
            encrypted = Convert.FromHexString(encoded);
            plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            if (plaintext.Length % 2 != 0 || plaintext.Length > 16384) throw new FormatException();
            // Native RDP uses UTF-16LE. Remove terminators only; spaces can be
            // meaningful password characters. Do not store the plaintext on
            // the profile, in logs, or in Windows Credential Manager.
            return new UnicodeEncoding(false, false, true).GetString(plaintext).TrimEnd('\0');
        }
        catch (Exception error) when (error is CryptographicException or FormatException or DecoderFallbackException)
        {
            // No raw data, account identity, file path, or exception payload.
            throw new RdpFilePasswordException();
        }
        finally
        {
            if (encrypted != null) CryptographicOperations.ZeroMemory(encrypted);
            if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}

internal sealed class RdpFilePasswordException : InvalidOperationException
{
    internal RdpFilePasswordException() : base("无法读取连接文件中的加密密码。请在生成该文件的电脑及同一 Windows 用户下重新发起连接；不要使用其他账号运行 PureRDP。") { }
}
