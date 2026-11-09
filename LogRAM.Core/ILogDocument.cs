using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LogRAM;

public interface ILogDocument : IDisposable
{
    string FilePath { get; }
    long FileSize { get; }
    long LineCount { get; }
    long MemoryUsage { get; }
    LogTextEncoding EncodingKind { get; }

    Task<LogPage> ReadPageAsync(long startLineNumber, int maxLines, CancellationToken cancellationToken);
    Task<LogFileDocument.AppendResult> RefreshAsync();
    Task<LogSearchSummary> SearchAsync(string pattern, bool useRegex, bool caseSensitive,
        Action<IReadOnlyList<LogSearchResult>> onBatch, IProgress<LogSearchProgress>? progress, CancellationToken cancellationToken);
    Task<LogSearchSummary> AdvancedSearchAsync(IReadOnlyList<string> includeTerms, IReadOnlyList<string> excludeTerms,
        bool caseSensitive, Action<IReadOnlyList<LogSearchResult>> onBatch, IProgress<LogSearchProgress>? progress, CancellationToken cancellationToken);
    Task<LogSearchSummary> SearchLinesAsync(long firstLineNumber, string pattern, bool useRegex, bool caseSensitive,
        Action<IReadOnlyList<LogSearchResult>> onBatch, CancellationToken cancellationToken);
    Task<LogSearchSummary> AdvancedSearchLinesAsync(long firstLineNumber, IReadOnlyList<string> includeTerms,
        IReadOnlyList<string> excludeTerms, bool caseSensitive, Action<IReadOnlyList<LogSearchResult>> onBatch, CancellationToken cancellationToken);
}
