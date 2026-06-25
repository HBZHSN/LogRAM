using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace LogRAM;

public sealed class AdvancedSearchQuery
{
    private static readonly Regex IncludeSegment = new(
        @"^in\s*\((.*)\)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex ExcludeSegment = new(
        @"^not\s*in\s*\((.*)\)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private AdvancedSearchQuery(IReadOnlyList<string> includes, IReadOnlyList<string> excludes)
    {
        Includes = includes;
        Excludes = excludes;
    }

    public IReadOnlyList<string> Includes { get; }

    public IReadOnlyList<string> Excludes { get; }

    public static bool TryParse(string? text, out AdvancedSearchQuery query)
    {
        query = new AdvancedSearchQuery(Array.Empty<string>(), Array.Empty<string>());

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var includes = new List<string>();
        var excludes = new List<string>();
        var matchedAny = false;

        foreach (var rawSegment in text.Split(';'))
        {
            var segment = rawSegment.Trim();
            if (segment.Length == 0)
            {
                continue;
            }

            var excludeMatch = ExcludeSegment.Match(segment);
            if (excludeMatch.Success)
            {
                AddTerms(excludes, excludeMatch.Groups[1].Value);
                matchedAny = true;
                continue;
            }

            var includeMatch = IncludeSegment.Match(segment);
            if (includeMatch.Success)
            {
                AddTerms(includes, includeMatch.Groups[1].Value);
                matchedAny = true;
                continue;
            }

            return false;
        }

        if (!matchedAny || (includes.Count == 0 && excludes.Count == 0))
        {
            return false;
        }

        query = new AdvancedSearchQuery(includes, excludes);
        return true;
    }

    public static string Format(IReadOnlyList<string> includes, IReadOnlyList<string> excludes)
    {
        var parts = new List<string>(2);
        if (includes.Count > 0)
        {
            parts.Add($"in({string.Join(",", includes)})");
        }

        if (excludes.Count > 0)
        {
            parts.Add($"notin({string.Join(",", excludes)})");
        }

        return string.Join(";", parts);
    }

    private static void AddTerms(List<string> target, string inside)
    {
        foreach (var part in inside.Split(','))
        {
            var term = part.Trim();
            if (term.Length > 0)
            {
                target.Add(term);
            }
        }
    }
}
