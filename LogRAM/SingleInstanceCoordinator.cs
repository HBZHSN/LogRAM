using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace LogRAM;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private bool _ownsMutex;

    public SingleInstanceCoordinator()
    {
        var user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var instanceName = $"LogRAM.{user}.{Process.GetCurrentProcess().SessionId}";
        _pipeName = instanceName;
        _mutex = new Mutex(false, $@"Local\{instanceName}");
        try
        {
            _ownsMutex = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }
    }

    public bool IsPrimary => _ownsMutex;

    public void Start(Action<string?> handleRequest)
    {
        if (_ownsMutex)
        {
            _ = ListenAsync(handleRequest, _stop.Token);
        }
    }

    public async Task<bool> TryForwardAsync(IEnumerable<string> filePaths)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var client = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);
            await client.ConnectAsync(timeout.Token);
            await using var writer = new StreamWriter(client) { AutoFlush = true };

            var sentFile = false;
            foreach (var filePath in filePaths)
            {
                await writer.WriteLineAsync(filePath);
                sentFile = true;
            }

            if (!sentFile)
            {
                await writer.WriteLineAsync(string.Empty);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ListenAsync(Action<string?> handleRequest, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server);
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    handleRequest(line.Length == 0 ? null : line);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
            _ownsMutex = false;
        }

        _mutex.Dispose();
    }
}
