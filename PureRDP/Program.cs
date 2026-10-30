using PureRDP.UI;
using System;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PureRDP;

internal static class Program
{
    private const string SingleInstanceMutexName = "Local\\PureRDP.SingleInstance";
    private const string SingleInstancePipeName = "PureRDP.SingleInstance";

    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == Services.MstscProxyService.ConfigureArgument)
        {
            try
            {
                if (args[1] is not ("enable" or "disable")) throw new ArgumentException("无效的代理配置操作。");
                Services.MstscProxyService.Configure(args[1] == "enable");
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "PureRDP · 代理设置未完成", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.ExitCode = 1;
            }
            return;
        }
        try
        {
            if (args.Length > 0 && args[0] == Services.MstscProxyService.ProxyArgument &&
                Environment.GetEnvironmentVariable(Services.NativeMstscLauncher.GuardVariable) == "1")
                throw new ArgumentException("原生客户端启动仍被系统重定向，已停止重复启动。请关闭 MSTSC 启动代理后重试。");
            var launch = Services.RdpCompatibilityService.Plan(args, Services.GlobalSettingsService.Load().RdpProxy.PreferNativeClient);
            if (launch != null)
            {
                Environment.ExitCode = Services.NativeMstscLauncher.Run(launch);
                return;
            }
            args = Services.MstscProxyService.NormalizeArguments(args);
        }
        catch (ArgumentException error)
        {
            MessageBox.Show(error.Message, "PureRDP · MSTSC 启动代理", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("原生远程桌面兼容启动未完成。请关闭 MSTSC 启动代理后重试。", "PureRDP · 连接未打开",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Environment.ExitCode = 1;
            return;
        }
        using var instanceMutex = new Mutex(true, SingleInstanceMutexName, out var isPrimaryInstance);
        if (!isPrimaryInstance)
        {
            SingleInstanceRelay.TryForward(SingleInstancePipeName, args);
            return;
        }

        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        using var rdpClient = new FormRdpClient(args);
        // Create the UI handle before accepting wake-up requests. During a
        // cold login a second launch can arrive before Application.Run.
        _ = rdpClient.Handle;
        using var instanceRelay = new SingleInstanceRelay(SingleInstancePipeName,
            forwardedArgs =>
            {
                if (rdpClient.IsDisposed || !rdpClient.IsHandleCreated)
                    return Task.CompletedTask;

                var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                try
                {
                    rdpClient.BeginInvoke(new Action(() =>
                    {
                        try { rdpClient.HandleSingleInstanceCommand(forwardedArgs); }
                        finally { accepted.TrySetResult(); }
                    }));
                }
                catch (InvalidOperationException) { accepted.TrySetResult(); }
                return accepted.Task;
            });
        Application.Run(rdpClient);
        rdpClient.Dispose();
        PureRDP.Services.ConnectionLogService.Flush();
    }

    private sealed class SingleInstanceRelay : IDisposable
    {
        private readonly string _pipeName;
        private readonly Func<string[], Task> _onArguments;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _listener;

        internal SingleInstanceRelay(string pipeName, Func<string[], Task> onArguments)
        {
            _pipeName = pipeName;
            _onArguments = onArguments;
            _listener = ListenAsync();
        }

        internal static void TryForward(string pipeName, string[] args)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                client.Connect(8000);
                using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: false);
                writer.WriteLine(JsonSerializer.Serialize(args));
                writer.Flush();
                // A bastion launcher may delete its temporary .rdp as soon as
                // this child exits. Wait until the primary UI has consumed it.
                using var reader = new StreamReader(client, Encoding.UTF8, false, 4096, leaveOpen: true);
                reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
            }
            catch (Exception error) when (error is TimeoutException or IOException or UnauthorizedAccessException)
            {
                MessageBox.Show("PureRDP 已在运行，但未能唤醒主控制台。\n\n" +
                    "程序可能仍在启动，请稍后重试，或左键单击托盘中的 PureRDP 图标。\n" +
                    "若仍无响应，请退出旧进程后重新打开；请先确认远程连接已保存。\n\n" +
                    "详细信息：" + error.Message,
                    "PureRDP · 唤醒失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private async Task ListenAsync()
        {
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    try
                    {
                    using var server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_cancellation.Token).ConfigureAwait(false);
                    using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 4096, leaveOpen: false);
                    var payload = await reader.ReadLineAsync(_cancellation.Token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(payload))
                        await _onArguments(Array.Empty<string>()).WaitAsync(_cancellation.Token).ConfigureAwait(false);
                    else
                    {
                        var forwardedArgs = JsonSerializer.Deserialize<string[]>(payload);
                        if (forwardedArgs != null)
                            await _onArguments(forwardedArgs).WaitAsync(_cancellation.Token).ConfigureAwait(false);
                    }
                    using var acknowledgement = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true);
                    await acknowledgement.WriteLineAsync("accepted").ConfigureAwait(false);
                    await acknowledgement.FlushAsync().ConfigureAwait(false);
                    }
                    catch (IOException) when (!_cancellation.IsCancellationRequested)
                    {
                        // A launcher closing its pipe must not permanently stop
                        // the primary instance's console wake-up listener.
                    }
                    catch (JsonException) { }
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
            catch (IOException) when (_cancellation.IsCancellationRequested) { }
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            try { _listener.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            _cancellation.Dispose();
        }
    }
}
