using System.Text;

namespace LogRAM.Npp;

internal sealed class LogSession(ILogDocument document) : IDisposable
{
    private readonly object _resultLock = new();
    private readonly List<long> _matches = new();
    private readonly Dictionary<long, string> _remoteResultTexts = new();
    private readonly Dictionary<long, string> _pageLines = new();
    private long _cachedPageStart, _cachedPageLastLine;
    private int _cachedPageMaxLines;
    internal ILogDocument Document { get; } = document;
    internal bool IsRemote => Document is SshLogDocument;
    internal SshLogDocument? RemoteDocument => Document as SshLogDocument;
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
    internal long TotalMatchCount { get; private set; }
    internal long MatchLine(int index) { lock (_resultLock) return _matches[index]; }

    internal bool HasSourcePage(long startLine, int rowCount)
    {
        if (Document is LogFileDocument || Document.LineCount <= 0) return true;
        startLine = Math.Clamp(startLine, 1, Document.LineCount);
        var neededEnd = Math.Min(Document.LineCount, startLine + Math.Max(1, rowCount) - 1);
        lock (_resultLock)
            return _cachedPageMaxLines > 0 && startLine >= _cachedPageStart &&
                (neededEnd <= _cachedPageLastLine || (startLine == _cachedPageStart && rowCount <= _cachedPageMaxLines));
    }

    internal async Task FetchSourcePageAsync(long startLine, int maxLines, CancellationToken token)
    {
        if (Document is LogFileDocument || Document.LineCount <= 0) return;
        startLine = Math.Clamp(startLine, 1, Document.LineCount);
        maxLines = Math.Clamp(maxLines, 1, 500);
        var page = await Document.ReadPageAsync(startLine, maxLines, token);
        lock (_resultLock)
        {
            _pageLines.Clear();
            foreach (var line in page.Lines) _pageLines[line.LineNumber] = line.Text;
            _cachedPageStart = startLine;
            _cachedPageMaxLines = maxLines;
            _cachedPageLastLine = page.Lines.Count > 0 ? page.Lines[^1].LineNumber : startLine;
        }
    }

    internal string GetSourceLineText(long lineNumber)
    {
        if (Document is LogFileDocument local) return local.GetLineText(lineNumber);
        lock (_resultLock) return _pageLines.GetValueOrDefault(lineNumber, "");
    }

    internal string GetResultLineText(int matchIndex)
    {
        var line = MatchLine(matchIndex);
        if (Document is LogFileDocument local) return local.GetLineText(line);
        lock (_resultLock) return _remoteResultTexts.GetValueOrDefault(line, "");
    }

    internal async Task<long> SearchAsync(string text, bool regex, bool caseSensitive, CancellationToken token,
        IProgress<LogSearchProgress>? progress = null)
    {
        var query = !regex && AdvancedSearchQuery.TryParse(text, out var advanced)
            ? new LogQuery { IncludeAny = advanced.Includes, Exclude = advanced.Excludes, CaseSensitive = caseSensitive }
            : new LogQuery { Pattern = text, UseRegex = regex, CaseSensitive = caseSensitive };
        lock (_resultLock) { _matches.Clear(); _remoteResultTexts.Clear(); }
        TotalMatchCount = 0;
        ResultsTop = 0;
        QueryText = text;
        Regex = regex;
        CaseSensitive = caseSensitive;
        _activeQuery = null;
        _pendingRefreshFrom = null;
        try
        {
            long count;
            if (Document is LogFileDocument local)
            {
                count = await LogQueryEngine.SearchAsync(local, query, match =>
                {
                    lock (_resultLock) _matches.Add(match.LineNumber);
                }, token, progress);
            }
            else
            {
                Action<IReadOnlyList<LogSearchResult>> onBatch = batch =>
                {
                    lock (_resultLock)
                    {
                        foreach (var item in batch)
                        {
                            _matches.Add(item.LineNumber);
                            _remoteResultTexts[item.LineNumber] = item.Text;
                        }
                    }
                };
                var summary = query.IncludeAny.Count > 0 || query.Exclude.Count > 0
                    ? await Document.AdvancedSearchAsync(query.IncludeAny, query.Exclude, caseSensitive, onBatch, progress, token)
                    : await Document.SearchAsync(text, regex, caseSensitive, onBatch, progress, token);
                count = summary.MatchCount;
            }
            TotalMatchCount = count;
            _activeQuery = query;
            _pendingRefreshFrom = null;
            return count;
        }
        catch (OperationCanceledException)
        {
            lock (_resultLock) if (TotalMatchCount < _matches.Count) TotalMatchCount = _matches.Count;
            throw;
        }
    }

    internal async Task<LogFileDocument.AppendResult> RefreshAsync(CancellationToken token)
    {
        LogFileDocument.AppendResult appended;
        IsRefreshing = true;
        try
        {
            appended = Document is LogFileDocument local
                ? await Task.Run(local.AppendNewContent, token)
                : await Document.RefreshAsync();
        }
        finally { IsRefreshing = false; }
        if (IsRemote && (appended.HasNewContent || appended.IsTruncated))
        {
            lock (_resultLock)
            {
                if (appended.IsTruncated || Math.Max(1, appended.OldLineCount) <= _cachedPageStart + _cachedPageMaxLines)
                {
                    _cachedPageMaxLines = 0;
                    _pageLines.Clear();
                }
            }
        }
        if (appended.IsTruncated || _activeQuery is null || (!appended.HasNewContent && !HasPendingRefresh)) return appended;
        var first = _pendingRefreshFrom ?? Math.Max(1, appended.OldLineCount);
        _pendingRefreshFrom = first;
        lock (_resultLock)
        {
            var remove = _matches.BinarySearch(first);
            if (remove < 0) remove = ~remove;
            for (var i = remove; i < _matches.Count; i++) _remoteResultTexts.Remove(_matches[i]);
            _matches.RemoveRange(remove, _matches.Count - remove);
        }
        if (Document is LogFileDocument localRefresh)
        {
            var query = new LogQuery
            {
                Pattern = _activeQuery.Pattern, IncludeAny = _activeQuery.IncludeAny, IncludeAll = _activeQuery.IncludeAll,
                Exclude = _activeQuery.Exclude, UseRegex = _activeQuery.UseRegex, CaseSensitive = _activeQuery.CaseSensitive, StartLine = first
            };
            await LogQueryEngine.SearchAsync(localRefresh, query, match =>
            {
                lock (_resultLock) _matches.Add(match.LineNumber);
            }, token);
            lock (_resultLock) TotalMatchCount = _matches.Count;
        }
        else
        {
            Action<IReadOnlyList<LogSearchResult>> onBatch = batch =>
            {
                lock (_resultLock)
                {
                    foreach (var item in batch)
                    {
                        _matches.Add(item.LineNumber);
                        _remoteResultTexts[item.LineNumber] = item.Text;
                    }
                }
            };
            var summary = _activeQuery.IncludeAny.Count > 0 || _activeQuery.Exclude.Count > 0
                ? await Document.AdvancedSearchLinesAsync(first, _activeQuery.IncludeAny, _activeQuery.Exclude, _activeQuery.CaseSensitive, onBatch, token)
                : await Document.SearchLinesAsync(first, _activeQuery.Pattern ?? "", _activeQuery.UseRegex, _activeQuery.CaseSensitive, onBatch, token);
            TotalMatchCount = summary.MatchCount;
        }
        _pendingRefreshFrom = null;
        return appended;
    }

    internal async Task ExportAsync(string path, CancellationToken token)
    {
        path = Path.GetFullPath(path);
        if (!IsRemote && path.Equals(Path.GetFullPath(Document.FilePath), StringComparison.OrdinalIgnoreCase))
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
                    await writer.WriteLineAsync($"{line}\t{GetResultLineText(i)}");
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
