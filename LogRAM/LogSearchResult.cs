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

    internal void RefreshFrom(LogSearchResult result)
    {
        _document = result._document;
        _text = result._text;
    }
}
