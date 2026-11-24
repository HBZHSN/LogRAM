using System;

namespace LogRAM;

public sealed class LogSearchResult
{
    private LogFileDocument? _document;
    private string? _text;

    internal LogSearchResult(long lineNumber, long offset, LogFileDocument document)
    {
        LineNumber = lineNumber;
        Offset = offset;
        _document = document;
    }

    internal LogSearchResult(long lineNumber, long offset, string text)
    {
        LineNumber = lineNumber;
        Offset = offset;
        _text = text;
    }

    public long LineNumber { get; }

    public long Offset { get; }

    public string Text => _text ?? (_document is null ? string.Empty : _text = _document.GetLineText(LineNumber));

    internal void SetDocument(LogFileDocument? document)
    {
        _document = document;
    }

    internal void InvalidateText()
    {
        _text = null;
    }

    internal bool MatchesText(LogFileDocument.LineTextMatcher? matcher, string query, StringComparison comparison)
    {
        if (_text is not null)
        {
            return _text.Contains(query, comparison);
        }

        if (_document is not null && matcher.HasValue)
        {
            return matcher.Value.Matches(_document, LineNumber);
        }

        return Text.Contains(query, comparison);
    }

    internal LogSearchResult ToSnapshot()
    {
        return new LogSearchResult(LineNumber, Offset, Text);
    }

    internal void RefreshFrom(LogSearchResult result)
    {
        _document = result._document;
        _text = result._text;
    }
}
