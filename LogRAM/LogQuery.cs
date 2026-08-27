using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LogRAM;

public sealed class LogQuery
{
    public string? Pattern { get; init; }

    public IReadOnlyList<string> IncludeAny { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> IncludeAll { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Exclude { get; init; } = Array.Empty<string>();

    public bool UseRegex { get; init; }

    public bool CaseSensitive { get; init; }

    public long StartLine { get; init; } = 1;

    public long? EndLine { get; init; }
}

public sealed record LogQueryLine(long LineNumber, string Text, bool IsMatch);

public static class LogQueryEngine
{
    public static async Task<long> SearchAsync(
        LogFileDocument document,
        LogQuery query,
        Action<LogSearchResult> onMatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(onMatch);

        var any = Clean(query.IncludeAny);
        var all = Clean(query.IncludeAll);
        var excludes = Clean(query.Exclude);
        var pattern = string.IsNullOrWhiteSpace(query.Pattern) ? null : query.Pattern;
        if (pattern is null && any.Length == 0 && all.Length == 0 && excludes.Length == 0)
        {
            throw new ArgumentException("At least one query, include, or exclude term is required.", nameof(query));
        }

        if (query.StartLine < 1 || query.EndLine < query.StartLine)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "The line range is invalid.");
        }

        var comparison = query.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var matchCount = 0L;

        void AcceptBatch(IReadOnlyList<LogSearchResult> batch, bool coreHandledAnyAndExclude)
        {
            foreach (var result in batch)
            {
                var text = result.Text;
                if ((!coreHandledAnyAndExclude && any.Length > 0 && !any.Any(term => text.Contains(term, comparison))) ||
                    all.Any(term => !text.Contains(term, comparison)) ||
                    (!coreHandledAnyAndExclude && excludes.Any(term => text.Contains(term, comparison))))
                {
                    continue;
                }

                matchCount++;
                onMatch(result);
            }
        }

        var fullFile = query.StartLine == 1 && query.EndLine is null;
        if (pattern is not null)
        {
            void OnBatch(IReadOnlyList<LogSearchResult> batch) => AcceptBatch(batch, coreHandledAnyAndExclude: false);
            if (fullFile)
            {
                await document.SearchAsync(pattern, query.UseRegex, query.CaseSensitive, OnBatch, null, cancellationToken);
            }
            else
            {
                await document.SearchLinesAsync(query.StartLine, query.EndLine, pattern, query.UseRegex, query.CaseSensitive, OnBatch, cancellationToken);
            }
        }
        else if (any.Length > 0 || excludes.Length > 0)
        {
            if (CanUseAdvancedSearch(any, excludes))
            {
                void OnBatch(IReadOnlyList<LogSearchResult> batch) => AcceptBatch(batch, coreHandledAnyAndExclude: true);
                if (fullFile)
                {
                    await document.AdvancedSearchAsync(any, excludes, query.CaseSensitive, OnBatch, null, cancellationToken);
                }
                else
                {
                    await document.AdvancedSearchLinesAsync(query.StartLine, query.EndLine, any, excludes, query.CaseSensitive, OnBatch, cancellationToken);
                }
            }
            else
            {
                var fallbackPattern = any.Length switch
                {
                    0 => ".*",
                    1 => any[0],
                    _ => string.Join('|', any.Select(Regex.Escape))
                };
                var fallbackRegex = any.Length != 1;
                void OnBatch(IReadOnlyList<LogSearchResult> batch) => AcceptBatch(batch, coreHandledAnyAndExclude: false);
                if (fullFile)
                {
                    await document.SearchAsync(fallbackPattern, fallbackRegex, query.CaseSensitive, OnBatch, null, cancellationToken);
                }
                else
                {
                    await document.SearchLinesAsync(query.StartLine, query.EndLine, fallbackPattern, fallbackRegex, query.CaseSensitive, OnBatch, cancellationToken);
                }
            }
        }
        else
        {
            void OnBatch(IReadOnlyList<LogSearchResult> batch) => AcceptBatch(batch, coreHandledAnyAndExclude: false);
            var anchor = all.MaxBy(term => term.Length)!;
            if (fullFile)
            {
                await document.SearchAsync(anchor, useRegex: false, query.CaseSensitive, OnBatch, null, cancellationToken);
            }
            else
            {
                await document.SearchLinesAsync(query.StartLine, query.EndLine, anchor, useRegex: false, query.CaseSensitive, OnBatch, cancellationToken);
            }
        }

        return matchCount;
    }

    public static IReadOnlyList<LogQueryLine> ReadContext(
        LogFileDocument document,
        IReadOnlyCollection<long> matchLineNumbers,
        int before,
        int after)
    {
        if (before < 0 || after < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(before), "Context line counts cannot be negative.");
        }

        var matches = new HashSet<long>(matchLineNumbers);
        var lines = new SortedSet<long>();
        foreach (var match in matches)
        {
            var first = Math.Max(1, match - before);
            var last = Math.Min(document.LineCount, match + after);
            for (var line = first; line <= last; line++)
            {
                lines.Add(line);
            }
        }

        return lines.Select(line => new LogQueryLine(line, document.GetLineText(line), matches.Contains(line))).ToArray();
    }

    private static string[] Clean(IReadOnlyList<string>? terms)
    {
        return terms?
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? Array.Empty<string>();
    }

    private static bool CanUseAdvancedSearch(IEnumerable<string> includes, IEnumerable<string> excludes)
    {
        return includes.Concat(excludes).All(term => term.All(ch => ch <= '\x7F'));
    }
}
