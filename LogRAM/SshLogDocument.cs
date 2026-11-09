using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LogRAM;

internal sealed class SshLogDocument(SshTarget target, SshSession session) : ILogDocument
{
    internal SshTarget Target { get; private set; } = target;
    public string FilePath => Target.DisplayPath;
    public long FileSize { get; private set; }
    public long LineCount { get; private set; }
    public long MemoryUsage => MemoryStatus?.ProcessBytes ?? 0;
    internal SshMemoryStatus? MemoryStatus => session.IsConnected ? session.MemoryStatus : null;
    internal Task RefreshMemoryStatusAsync() => session.RefreshMemoryStatusAsync();
    public LogTextEncoding EncodingKind { get; private set; }
    internal long MatchCount { get; private set; }
    internal int ReturnedCount { get; private set; }
    private readonly HashSet<long> _returnedMatches = new();
    private long _searchedThrough;
    private bool _lastLineMatched;

    internal static async Task<SshLogDocument> OpenAsync(SshTarget target, LogTextEncoding encoding, CancellationToken token = default)
    {
        var session = await SshSession.ConnectAsync(target.ConfigPath, target.Host, token).ConfigureAwait(false);
        var document = new SshLogDocument(target, session);
        try
        {
            await document.OpenAsync(encoding, token).ConfigureAwait(false);
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    internal async Task OpenAsync(LogTextEncoding encoding, CancellationToken token)
    {
        var metadata = await session.RequestAsync("open", new { path = Target.Path, encoding = encoding == LogTextEncoding.Gbk ? "gbk" : "utf8" }, token).ConfigureAwait(false);
        Target = Target with { Path = metadata.GetProperty("path").GetString()! };
        FileSize = metadata.GetProperty("fileSize").GetInt64();
        LineCount = metadata.GetProperty("lineCount").GetInt64();
        EncodingKind = encoding;
    }

    public async Task<LogPage> ReadPageAsync(long startLineNumber, int maxLines, CancellationToken cancellationToken)
    {
        var page = await session.RequestAsync("read", new { startLine = startLineNumber, maxLines = Math.Clamp(maxLines, 1, 500) }, cancellationToken).ConfigureAwait(false);
        return page.Deserialize<LogPage>(SshSession.JsonOptions)!;
    }

    public async Task<LogFileDocument.AppendResult> RefreshAsync()
    {
        var data = await session.RequestAsync("refresh", new { }).ConfigureAwait(false);
        var result = data.Deserialize<LogFileDocument.AppendResult>(SshSession.JsonOptions);
        if (!result.IsTruncated)
        {
            FileSize = result.NewFileSize;
            LineCount = result.NewLineCount;
        }
        return result;
    }

    private async Task<LogSearchSummary> SearchCoreAsync(string pattern, bool useRegex, bool caseSensitive,
        IReadOnlyList<string> includes, IReadOnlyList<string> excludes, long startLine,
        Action<IReadOnlyList<LogSearchResult>> onBatch, IProgress<LogSearchProgress>? progress, CancellationToken token, bool incremental = false)
    {
        var maxResults = incremental ? Math.Min(1000, 1000 - _returnedMatches.Count + 1) : 1000;
        var data = await session.RequestAsync("search", new { pattern, useRegex, caseSensitive, includes, excludes, startLine, maxResults }, token, progress).ConfigureAwait(false);
        if (!incremental) _returnedMatches.Clear();
        else _returnedMatches.Remove(startLine);
        var results = data.GetProperty("results").EnumerateArray().Select(item => new LogSearchResult(
            item.GetProperty("lineNumber").GetInt64(), item.GetProperty("offset").GetInt64(), item.GetProperty("text").GetString()!)).ToArray();
        MatchCount = (incremental && startLine == _searchedThrough ? MatchCount - (_lastLineMatched ? 1 : 0) : 0)
            + data.GetProperty("matchCount").GetInt64();
        _searchedThrough = LineCount;
        _lastLineMatched = data.GetProperty("lastLineMatched").GetBoolean();
        var accepted = new List<LogSearchResult>();
        foreach (var result in results)
        {
            if (!_returnedMatches.Contains(result.LineNumber) && _returnedMatches.Count >= 1000) continue;
            _returnedMatches.Add(result.LineNumber);
            accepted.Add(result);
        }
        ReturnedCount = _returnedMatches.Count;
        onBatch(accepted);
        return new LogSearchSummary(MatchCount);
    }

    public Task<LogSearchSummary> SearchAsync(string pattern, bool useRegex, bool caseSensitive,
        Action<IReadOnlyList<LogSearchResult>> onBatch, IProgress<LogSearchProgress>? progress, CancellationToken cancellationToken) =>
        SearchCoreAsync(pattern, useRegex, caseSensitive, [], [], 1, onBatch, progress, cancellationToken);

    public Task<LogSearchSummary> AdvancedSearchAsync(IReadOnlyList<string> includeTerms, IReadOnlyList<string> excludeTerms,
        bool caseSensitive, Action<IReadOnlyList<LogSearchResult>> onBatch, IProgress<LogSearchProgress>? progress, CancellationToken cancellationToken) =>
        SearchCoreAsync("", false, caseSensitive, includeTerms, excludeTerms, 1, onBatch, progress, cancellationToken);

    public Task<LogSearchSummary> SearchLinesAsync(long firstLineNumber, string pattern, bool useRegex, bool caseSensitive,
        Action<IReadOnlyList<LogSearchResult>> onBatch, CancellationToken cancellationToken) =>
        SearchCoreAsync(pattern, useRegex, caseSensitive, [], [], firstLineNumber, onBatch, null, cancellationToken, incremental: true);

    public Task<LogSearchSummary> AdvancedSearchLinesAsync(long firstLineNumber, IReadOnlyList<string> includeTerms,
        IReadOnlyList<string> excludeTerms, bool caseSensitive, Action<IReadOnlyList<LogSearchResult>> onBatch, CancellationToken cancellationToken) =>
        SearchCoreAsync("", false, caseSensitive, includeTerms, excludeTerms, firstLineNumber, onBatch, null, cancellationToken, incremental: true);

    public void Dispose() => session.Dispose();
}
