using PureRDP.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace PureRDP.Services;

// One external launch, one fallback. All clones share this state so a duplicate
// event or a successful session can never launch an extra native connection.
internal sealed class NativeRdpFallback : IDisposable
{
    private byte[]? _fileBytes;
    private readonly string? _sourceName;
    private readonly string[] _arguments;
    private int _state; // 0 available, 1 claimed, 2 completed/canceled

    private NativeRdpFallback(string? source, byte[]? bytes, string[] arguments)
    { _sourceName = source == null ? null : Path.GetFileName(source); _fileBytes = bytes; _arguments = arguments; }

    internal static NativeRdpFallback Capture(ApplicationSettings settings, string[] args)
    {
        var native = new List<string>();
        byte[]? bytes = null;
        var file = settings.RdpFilePath;
        if (file != null)
        {
            if (new FileInfo(file).Length > 1024 * 1024) throw new InvalidDataException("连接文件过大，无法保留失败切换信息。");
            bytes = File.ReadAllBytes(file);
            native.Add(file);
        }
        foreach (var argument in args)
        {
            foreach (var pair in new[] { ("--hostname=", "/v:"), ("--width=", "/w:"), ("--height=", "/h:") })
                if (argument.StartsWith(pair.Item1, StringComparison.Ordinal)) native.Add(pair.Item2 + argument[pair.Item1.Length..]);
            if (argument is "--fullscreen" or "-f") native.Add("/f");
            if (argument == "--administrative-session") native.Add("/admin");
            if (argument == "--prompt-for-credentials") native.Add("/prompt");
        }
        if (file == null && !native.Exists(value => value.StartsWith("/v:", StringComparison.OrdinalIgnoreCase)))
            native.Add("/v:" + settings.Connection.Hostname);
        return new(file, bytes, native.ToArray());
    }

    internal bool TryClaim() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

    internal void CancelIfPending()
    {
        if (Interlocked.CompareExchange(ref _state, 2, 0) == 0) ClearBytes();
    }

    internal void Run(Action<NativeMstscLaunch>? launch = null)
    {
        string? directory = null;
        string? file = null;
        try
        {
            if (Volatile.Read(ref _state) != 1) return;
            var arguments = (string[])_arguments.Clone();
            if (_fileBytes != null)
            {
                // The assistant may have deleted its original by now. Only on
                // an actual failure materialize the byte-for-byte snapshot in
                // this user's TEMP, and delete it when the native client exits.
                directory = Path.Combine(Path.GetTempPath(), "PureRDP-fallback-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                file = Path.Combine(directory, _sourceName!);
                File.WriteAllBytes(file, _fileBytes);
                arguments[0] = file;
            }
            var request = new NativeMstscLaunch(NativeMstscLauncher.SystemExecutable, arguments);
            if (launch != null) launch(request);
            else NativeMstscLauncher.Run(request);
        }
        finally
        {
            try
            {
                if (file != null && File.Exists(file)) File.Delete(file);
                if (directory != null && Directory.Exists(directory)) Directory.Delete(directory);
            }
            finally { Dispose(); }
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _state, 2);
        ClearBytes();
    }

    private void ClearBytes()
    {
        var bytes = Interlocked.Exchange(ref _fileBytes, null);
        if (bytes != null) CryptographicOperations.ZeroMemory(bytes);
    }
    public override string ToString() => "External connection fallback (payload omitted)";
}
