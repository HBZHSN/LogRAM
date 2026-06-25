namespace LogRAM;

public sealed class LogSearchResult
{
    private readonly LogFileDocument _document;
    private string? _text;

    internal LogSearchResult(long lineNumber, long offset, LogFileDocument document)
    {
        LineNumber = lineNumber;
        Offset = offset;
        _document = document;
    }

    public long LineNumber { get; }

    public long Offset { get; }

    public string Text => _text ??= _document.GetLineText(LineNumber);
}
