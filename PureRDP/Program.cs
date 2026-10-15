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
                    return;

                try
                {
                    rdpClient.BeginInvoke(new Action(() => rdpClient.HandleSingleInstanceCommand(forwardedArgs)));
                }
                catch (InvalidOperationException) { }
            });
        Application.Run(rdpClient);
        rdpClient.Dispose();
        PureRDP.Services.ConnectionLogService.Flush();
    }

    private sealed class SingleInstanceRelay : IDisposable
    {
        private readonly string _pipeName;
        private readonly Action<string[]> _onArguments;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _listener;

        internal SingleInstanceRelay(string pipeName, Action<string[]> onArguments)
        {
            _pipeName = pipeName;
            _onArguments = onArguments;
            _listener = ListenAsync();
        }

        internal static void TryForward(string pipeName, string[] args)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                client.Connect(8000);
                using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: false);
                writer.WriteLine(JsonSerializer.Serialize(args));
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
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_cancellation.Token).ConfigureAwait(false);
                    using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 4096, leaveOpen: false);
                    var payload = await reader.ReadLineAsync(_cancellation.Token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(payload))
                        _onArguments(Array.Empty<string>());
                    else
                    {
                        var forwardedArgs = JsonSerializer.Deserialize<string[]>(payload);
                        if (forwardedArgs != null)
                            _onArguments(forwardedArgs);
                    }
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
