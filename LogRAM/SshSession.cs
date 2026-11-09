using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LogRAM;

internal sealed record SshTarget(string ConfigPath, string Host, string Path)
{
    public string DisplayPath => $"ssh://{Host}{(Path.StartsWith('/') ? "" : "/")}{Path}";

    internal static bool TryParseUri(string value, out SshTarget target)
    {
        target = default!;
        if (!value.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) return false;
        var rest = value[6..];
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash >= rest.Length - 1) return false;
        var path = rest[slash..];
        if (path.Length >= 3 && path[0] == '/' && path[2] == ':') path = path[1..];
        target = new SshTarget(SshConfig.DefaultPath, rest[..slash], path);
        return true;
    }
}

internal sealed record SshDirectoryEntry(string Name, string Path, bool IsDirectory, long Size)
{
    public string DisplayName => IsDirectory ? $"{Name}/" : $"{Name}  ({Size:n0} B)";
}

internal sealed record SshDirectory(string Path, string Parent, IReadOnlyList<SshDirectoryEntry> Entries, bool Limited, string Prefix);

internal sealed record SshMemoryStatus(long AvailableBytes, long ProcessBytes);

internal sealed class SshSession : IDisposable
{
    internal const string Bootstrap = "import base64,sys;exec(compile(base64.b64decode(sys.stdin.buffer.readline()),'<logram-ssh>','exec'))";
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly Process _process;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly CancellationTokenSource _closed = new();
    private readonly StringBuilder _errors = new();
    private long _nextId;
    private long _activeId;
    private int _disposed;

    internal bool IsConnected
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            try { return !_process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }
    internal SshMemoryStatus? MemoryStatus { get; private set; }

    internal async Task RefreshMemoryStatusAsync()
    {
        try { await RequestAsync("memory", new { }).ConfigureAwait(false); }
        catch { MemoryStatus = null; throw; }
    }

    private SshSession(ProcessStartInfo startInfo)
    {
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardInputEncoding = new UTF8Encoding(false);
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        _process = new Process { StartInfo = startInfo };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_errors)
            {
                _errors.AppendLine(e.Data);
                if (_errors.Length > 4096) _errors.Remove(0, _errors.Length - 4096);
            }
        };
        _process.Start();
        _process.BeginErrorReadLine();
    }

    internal static Task<SshSession> ConnectAsync(string configPath, string host, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(host) || host.StartsWith('-') || host.IndexOfAny([' ', '\t', '\r', '\n', '\0']) >= 0)
            throw new ArgumentException(Loc.S.SshInvalidHost);
        var startInfo = new ProcessStartInfo("ssh.exe");
        foreach (var argument in new[] { "-T", "-F", System.IO.Path.GetFullPath(configPath),
                     "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "-o", "ServerAliveInterval=15",
                     "-o", "ServerAliveCountMax=2", "--", host,
                     $"exec python3 -u -c \"{Bootstrap}\"" })
            startInfo.ArgumentList.Add(argument);
        return StartAsync(startInfo, token);
    }

    internal static async Task<SshSession> StartAsync(ProcessStartInfo startInfo, CancellationToken token)
    {
        var session = new SshSession(startInfo);
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token, session._closed.Token);
            startup.CancelAfter(TimeSpan.FromSeconds(25));
            using var script = typeof(SshSession).Assembly.GetManifestResourceStream("LogRAM.ssh_agent.py")
                ?? throw new InvalidOperationException("Missing SSH worker resource.");
            using var bytes = new MemoryStream();
            await script.CopyToAsync(bytes, startup.Token).ConfigureAwait(false);
            await session._process.StandardInput.WriteLineAsync(Convert.ToBase64String(bytes.ToArray()).AsMemory(), startup.Token).ConfigureAwait(false);
            await session._process.StandardInput.FlushAsync(startup.Token).ConfigureAwait(false);
            var hello = await session.ReadAsync(startup.Token).ConfigureAwait(false);
            if (!hello.TryGetProperty("ready", out var ready) || ready.GetInt32() != 1)
                throw new IOException("Unsupported SSH worker response.");
            return session;
        }
        catch (Exception ex)
        {
            session.Dispose();
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            throw new IOException(Loc.S.SshConnectionFailed(session.ErrorText(ex.Message)), ex);
        }
    }

    internal async Task<JsonElement> RequestAsync(string operation, object arguments, CancellationToken token = default,
        IProgress<LogSearchProgress>? progress = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _closed.Token);
        await _requests.WaitAsync(linked.Token).ConfigureAwait(false);
        var id = Interlocked.Increment(ref _nextId);
        Interlocked.Exchange(ref _activeId, id);
        try
        {
            await WriteAsync(new { id, op = operation, arguments }).ConfigureAwait(false);
            using var registration = token.Register(() => _ = CancelAsync(id));
            while (true)
            {
                var response = await ReadAsync(_closed.Token).ConfigureAwait(false);
                if (response.GetProperty("id").GetInt64() != id) throw new IOException("Unexpected SSH response ID.");
                if (response.TryGetProperty("memory", out var memory))
                    MemoryStatus = memory.Deserialize<SshMemoryStatus>(JsonOptions);
                if (response.TryGetProperty("progress", out var update))
                {
                    progress?.Report(update.Deserialize<LogSearchProgress>(JsonOptions)!);
                    continue;
                }
                if (response.TryGetProperty("cancelled", out _)) throw new OperationCanceledException(token);
                if (response.TryGetProperty("error", out var error)) throw new IOException(error.GetString());
                token.ThrowIfCancellationRequested();
                return response.GetProperty("result");
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException || ex is JsonException)
        {
            Dispose();
            throw new IOException(Loc.S.SshConnectionFailed(ErrorText(ex.Message)), ex);
        }
        finally
        {
            Interlocked.Exchange(ref _activeId, 0);
            _requests.Release();
        }
    }

    private async Task WriteAsync(object value)
    {
        await _writes.WaitAsync(_closed.Token).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions)).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(_closed.Token).ConfigureAwait(false);
        }
        finally { _writes.Release(); }
    }

    private async Task<JsonElement> ReadAsync(CancellationToken token)
    {
        var line = await _process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false);
        if (line is null)
        {
            await _process.WaitForExitAsync(token).ConfigureAwait(false);
            throw new EndOfStreamException(ErrorText("SSH connection closed."));
        }
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    private async Task CancelAsync(long id)
    {
        try
        {
            await WriteAsync(new { id, op = "cancel" }).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(5), _closed.Token).ConfigureAwait(false);
            if (Interlocked.Read(ref _activeId) == id) Dispose();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
    }

    private string ErrorText(string fallback)
    {
        lock (_errors) return _errors.Length == 0 ? fallback : _errors.ToString().Trim();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        MemoryStatus = null;
        _closed.Cancel();
        try
        {
            _process.StandardInput.Close();
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { }
        _process.Dispose();
    }
}
