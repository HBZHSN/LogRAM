using System.Text;

namespace LogRAM.Npp;

internal enum SidePaneTab
{
    Pinned,
    SecondarySearch
}

internal readonly record struct PinnedResultLine(long LineNumber, string Text);

internal sealed class LogSession(ILogDocument document) : IDisposable
{
    private readonly object _resultLock = new();
    private readonly List<long> _matches = new();
    private readonly Dictionary<long, string> _remoteResultTexts = new();
    private readonly Dictionary<long, string> _pageLines = new();
    private readonly List<PinnedResultLine> _pinned = new();
    private readonly HashSet<long> _pinnedLines = new();
    private readonly List<PinnedResultLine> _secondary = new();
    private readonly List<int> _resultFindMatches = new();
    private long _cachedPageStart, _cachedPageLastLine;
    private int _cachedPageMaxLines;
    internal ILogDocument Document { get; } = document;
    internal bool IsRemote => Document is SshLogDocument;
    internal SshLogDocument? RemoteDocument => Document as SshLogDocument;
    internal long SourceTop { get; set; } = 1;
    internal int ResultsTop { get; set; }
    internal int PinnedTop { get; set; }
    internal int SecondaryTop { get; set; }
    internal SidePaneTab ActiveSideTab { get; set; } = SidePaneTab.Pinned;
    internal string SecondaryQuery { get; private set; } = "";
    internal bool IsResultFindOpen { get; set; }
    internal string ResultFindQuery { get; set; } = "";
    internal bool ResultFindCaseSensitive { get; set; }
    internal int CurrentResultFindIndex { get; set; } = -1;
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
    internal int PinnedCount => _pinned.Count;
    internal int SecondaryCount => _secondary.Count;
    internal int ResultFindMatchCount => _resultFindMatches.Count;
    internal int ResultFindMatchAt(int index) => _resultFindMatches[index];
    internal PinnedResultLine PinnedAt(int index) => _pinned[index];
    internal PinnedResultLine SecondaryAt(int index) => _secondary[index];
    internal bool IsPinned(long lineNumber) => _pinnedLines.Contains(lineNumber);

    internal bool TogglePin(long lineNumber, string? text = null)
    {
        if (_pinnedLines.Remove(lineNumber))
        {
            for (var i = 0; i < _pinned.Count; i++)
            {
                if (_pinned[i].LineNumber == lineNumber)
                {
                    _pinned.RemoveAt(i);
                    break;
                }
            }
            return false;
        }

        string resolvedText;
        if (text is not null)
        {
            resolvedText = text;
        }
        else if (Document is LogFileDocument local)
        {
            resolvedText = local.GetLineText(lineNumber);
        }
        else
        {
            lock (_resultLock)
                resolvedText = _remoteResultTexts.TryGetValue(lineNumber, out var rt) ? rt : _pageLines.GetValueOrDefault(lineNumber, "");
        }

        _pinnedLines.Add(lineNumber);
        var insertAt = 0;
        while (insertAt < _pinned.Count && _pinned[insertAt].LineNumber < lineNumber) insertAt++;
        _pinned.Insert(insertAt, new PinnedResultLine(lineNumber, resolvedText));
        return true;
    }

    internal void ClearPinned()
    {
        _pinned.Clear();
        _pinnedLines.Clear();
        PinnedTop = 0;
    }

    internal void ClearSecondary()
    {
        _secondary.Clear();
        SecondaryQuery = "";
        SecondaryTop = 0;
    }

    internal void CopyPinnedFrom(LogSession previous)
    {
        _pinned.Clear();
        _pinnedLines.Clear();
        var local = Document as LogFileDocument;
        foreach (var item in previous._pinned)
        {
            if (item.LineNumber > Document.LineCount) continue;
            var text = local is not null ? local.GetLineText(item.LineNumber) : item.Text;
            _pinnedLines.Add(item.LineNumber);
            _pinned.Add(new PinnedResultLine(item.LineNumber, text));
        }
        PinnedTop = previous.PinnedTop;
        ActiveSideTab = SidePaneTab.Pinned;
    }

    internal int UpdateResultFindMatches(string query, bool caseSensitive, int preferredResultIndex = -1)
    {
        ResultFindQuery = query;
        ResultFindCaseSensitive = caseSensitive;
        _resultFindMatches.Clear();
        CurrentResultFindIndex = -1;
        if (string.IsNullOrEmpty(query)) return 0;

        long[] lines;
        lock (_resultLock) lines = _matches.ToArray();
        if (lines.Length == 0) return 0;

        if (Document is LogFileDocument local)
        {
            var matcher = local.CreateLineTextMatcher(query, caseSensitive);
            for (var i = 0; i < lines.Length; i++)
            {
                if (matcher.Matches(local, lines[i])) _resultFindMatches.Add(i);
            }
        }
        else
        {
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            lock (_resultLock)
            {
                for (var i = 0; i < lines.Length; i++)
                {
                    if (_remoteResultTexts.TryGetValue(lines[i], out var text) && text.Contains(query, comparison))
                        _resultFindMatches.Add(i);
                }
            }
        }

        if (_resultFindMatches.Count == 0) return 0;

        var chosen = -1;
        if (preferredResultIndex >= 0)
        {
            chosen = _resultFindMatches.BinarySearch(preferredResultIndex);
            if (chosen < 0) chosen = -1;
        }
        if (chosen < 0)
        {
            chosen = 0;
            for (var i = 0; i < _resultFindMatches.Count; i++)
            {
                if (_resultFindMatches[i] >= ResultsTop) { chosen = i; break; }
            }
        }
        CurrentResultFindIndex = chosen;
        return _resultFindMatches.Count;
    }

    internal int CommitSecondarySearchAll()
    {
        _secondary.Clear();
        var count = MatchCount;
        foreach (var idx in _resultFindMatches)
        {
            if (idx >= 0 && idx < count)
                _secondary.Add(new PinnedResultLine(MatchLine(idx), GetResultLineText(idx)));
        }
        SecondaryQuery = ResultFindQuery;
        SecondaryTop = 0;
        if (_secondary.Count > 0) ActiveSideTab = SidePaneTab.SecondarySearch;
        return _secondary.Count;
    }

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
        _resultFindMatches.Clear();
        CurrentResultFindIndex = -1;
        ClearSecondary();
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
        if (appended.IsTruncated) return appended;
        if (appended.HasNewContent && Document is LogFileDocument localTail)
        {
            var changedLine = Math.Max(1, appended.OldLineCount);
            if (_pinnedLines.Contains(changedLine))
            {
                for (var i = 0; i < _pinned.Count; i++)
                {
                    if (_pinned[i].LineNumber == changedLine)
                    {
                        _pinned[i] = new PinnedResultLine(changedLine, localTail.GetLineText(changedLine));
                        break;
                    }
                }
            }
        }
        if (_activeQuery is null || (!appended.HasNewContent && !HasPendingRefresh)) return appended;
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
