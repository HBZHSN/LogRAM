using System.Text;

namespace LogRAM.Npp;

internal sealed class LogSession(LogFileDocument document) : IDisposable
{
    private readonly object _resultLock = new();
    private readonly List<long> _matches = new();
    internal LogFileDocument Document { get; } = document;
    internal long SourceTop { get; set; } = 1;
    internal int ResultsTop { get; set; }
    internal string QueryText { get; set; } = "";
    internal bool Regex { get; set; }
    internal bool CaseSensitive { get; set; }
    internal bool Live { get; set; }
    private LogQuery? _activeQuery;
    private long? _pendingRefreshFrom;
    internal bool IsRefreshing { get; private set; }
    internal bool HasPendingRefresh => _pendingRefreshFrom.HasValue;
    internal int MatchCount { get { lock (_resultLock) return _matches.Count; } }
    internal long MatchLine(int index) { lock (_resultLock) return _matches[index]; }

    internal async Task<long> SearchAsync(string text, bool regex, bool caseSensitive, CancellationToken token,
        IProgress<LogSearchProgress>? progress = null)
    {
        var query = !regex && AdvancedSearchQuery.TryParse(text, out var advanced)
            ? new LogQuery { IncludeAny = advanced.Includes, Exclude = advanced.Excludes, CaseSensitive = caseSensitive }
            : new LogQuery { Pattern = text, UseRegex = regex, CaseSensitive = caseSensitive };
        lock (_resultLock) _matches.Clear();
        ResultsTop = 0;
        QueryText = text;
        Regex = regex;
        CaseSensitive = caseSensitive;
        _activeQuery = null;
        _pendingRefreshFrom = null;
        var count = await LogQueryEngine.SearchAsync(Document, query, match =>
        {
            lock (_resultLock) _matches.Add(match.LineNumber);
        }, token, progress);
        _activeQuery = query;
        _pendingRefreshFrom = null;
        return count;
    }

    internal async Task<LogFileDocument.AppendResult> RefreshAsync(CancellationToken token)
    {
        LogFileDocument.AppendResult appended;
        IsRefreshing = true;
        try { appended = await Task.Run(Document.AppendNewContent, token); }
        finally { IsRefreshing = false; }
        if (appended.IsTruncated || _activeQuery is null || (!appended.HasNewContent && !HasPendingRefresh)) return appended;
        var first = _pendingRefreshFrom ?? Math.Max(1, appended.OldLineCount);
        _pendingRefreshFrom = first;
        lock (_resultLock)
        {
            var remove = _matches.BinarySearch(first);
            if (remove < 0) remove = ~remove;
            _matches.RemoveRange(remove, _matches.Count - remove);
        }
        var query = new LogQuery
        {
            Pattern = _activeQuery.Pattern, IncludeAny = _activeQuery.IncludeAny, IncludeAll = _activeQuery.IncludeAll,
            Exclude = _activeQuery.Exclude, UseRegex = _activeQuery.UseRegex, CaseSensitive = _activeQuery.CaseSensitive, StartLine = first
        };
        await LogQueryEngine.SearchAsync(Document, query, match =>
        {
            lock (_resultLock) _matches.Add(match.LineNumber);
        }, token);
        _pendingRefreshFrom = null;
        return appended;
    }

    internal async Task ExportAsync(string path, CancellationToken token)
    {
        path = Path.GetFullPath(path);
        if (path.Equals(Path.GetFullPath(Document.FilePath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Cannot export over the source log.");
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false)))
            {
                for (var i = 0; i < MatchCount; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var line = MatchLine(i);
                    await writer.WriteLineAsync($"{line}\t{Document.GetLineText(line)}");
                }
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Dispose() => Document.Dispose();
}
