using System;
using System.Collections.Generic;

namespace LogRAM;

internal sealed class CliOptions
{
    public string FilePath { get; private set; } = string.Empty;

    public string? Pattern { get; private set; }

    public List<string> IncludeAny { get; } = new();

    public List<string> IncludeAll { get; } = new();

    public List<string> Exclude { get; } = new();

    public string? OutputPath { get; private set; }

    public bool UseRegex { get; private set; }

    public bool CaseSensitive { get; private set; }

    public bool ShowLineNumber { get; private set; }

    public bool Json { get; private set; }

    public bool CountOnly { get; private set; }

    public int Before { get; private set; }

    public int After { get; private set; }

    public int MaxCount { get; private set; } = int.MaxValue;

    public long StartLine { get; private set; } = 1;

    public long? EndLine { get; private set; }

    public LogTextEncoding Encoding { get; private set; } = LogTextEncoding.Utf8;

    public static bool LooksLikeCli(string[] args)
    {
        var positionals = 0;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-s":
                case "--search":
                case "-i":
                case "--any":
                case "--include-any":
                case "-a":
                case "--all":
                case "--include-all":
                case "-x":
                case "--exclude":
                case "-h":
                case "--help":
                    return true;
                case "-o":
                case "--output":
                case "-e":
                case "--encoding":
                case "-B":
                case "--before":
                case "-A":
                case "--after":
                case "-C":
                case "--context":
                case "-m":
                case "--max-count":
                case "--start-line":
                case "--end-line":
                    i++;
                    break;
                default:
                    if (!args[i].StartsWith('-'))
                    {
                        positionals++;
                    }

                    break;
            }
        }

        return positionals >= 2;
    }

    public static bool TryParse(string[] args, out CliOptions options, out string? error)
    {
        options = new CliOptions();
        error = null;
        var positionals = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-o":
                case "--output":
                    if (!Take(args, ref i, arg, out var output, out error)) return false;
                    options.OutputPath = output;
                    break;
                case "-s":
                case "--search":
                    if (!Take(args, ref i, arg, out var pattern, out error)) return false;
                    if (options.Pattern is not null)
                    {
                        error = "--search 只能指定一次；多关键词请使用 --any/--all。";
                        return false;
                    }

                    options.Pattern = pattern;
                    break;
                case "-i":
                case "--any":
                case "--include-any":
                    if (!Take(args, ref i, arg, out var any, out error)) return false;
                    options.IncludeAny.Add(any);
                    break;
                case "-a":
                case "--all":
                case "--include-all":
                    if (!Take(args, ref i, arg, out var all, out error)) return false;
                    options.IncludeAll.Add(all);
                    break;
                case "-x":
                case "--exclude":
                    if (!Take(args, ref i, arg, out var exclude, out error)) return false;
                    options.Exclude.Add(exclude);
                    break;
                case "-e":
                case "--encoding":
                    if (!Take(args, ref i, arg, out var encodingValue, out error)) return false;
                    if (!TryParseEncoding(encodingValue, out var encoding))
                    {
                        error = $"不支持的编码：{encodingValue}（可选 utf8 或 gbk）。";
                        return false;
                    }

                    options.Encoding = encoding;
                    break;
                case "-B":
                case "--before":
                    if (!TakeNonNegativeInt(args, ref i, arg, out var before, out error)) return false;
                    options.Before = before;
                    break;
                case "-A":
                case "--after":
                    if (!TakeNonNegativeInt(args, ref i, arg, out var after, out error)) return false;
                    options.After = after;
                    break;
                case "-C":
                case "--context":
                    if (!TakeNonNegativeInt(args, ref i, arg, out var context, out error)) return false;
                    options.Before = options.After = context;
                    break;
                case "-m":
                case "--max-count":
                    if (!TakePositiveInt(args, ref i, arg, out var maxCount, out error)) return false;
                    options.MaxCount = maxCount;
                    break;
                case "--start-line":
                    if (!TakePositiveLong(args, ref i, arg, out var startLine, out error)) return false;
                    options.StartLine = startLine;
                    break;
                case "--end-line":
                    if (!TakePositiveLong(args, ref i, arg, out var endLine, out error)) return false;
                    options.EndLine = endLine;
                    break;
                case "-r":
                case "--regex":
                    options.UseRegex = true;
                    break;
                case "-c":
                case "--case-sensitive":
                    options.CaseSensitive = true;
                    break;
                case "-n":
                case "--line-number":
                    options.ShowLineNumber = true;
                    break;
                case "--json":
                    options.Json = true;
                    break;
                case "--count-only":
                    options.CountOnly = true;
                    break;
                case "-h":
                case "--help":
                    return false;
                default:
                    if (arg.StartsWith('-'))
                    {
                        error = $"未知选项：{arg}";
                        return false;
                    }

                    positionals.Add(arg);
                    break;
            }
        }

        if (positionals.Count == 0)
        {
            error = "缺少文件路径参数。";
            return false;
        }

        if (positionals.Count > 2)
        {
            error = "位置参数过多；多关键词请使用 --any/--all。";
            return false;
        }

        options.FilePath = positionals[0];
        if (options.Pattern is null && positionals.Count == 2)
        {
            options.Pattern = positionals[1];
        }

        if (options.Pattern is null && options.IncludeAny.Count == 0 && options.IncludeAll.Count == 0 && options.Exclude.Count == 0)
        {
            error = "缺少搜索条件。";
            return false;
        }

        if (options.UseRegex && options.Pattern is null)
        {
            error = "--regex 需要 --search 或第二个位置参数。";
            return false;
        }

        if (options.EndLine < options.StartLine)
        {
            error = "--end-line 不能小于 --start-line。";
            return false;
        }

        if (options.Json && options.CountOnly)
        {
            error = "--json 与 --count-only 不能同时使用。";
            return false;
        }

        return true;
    }

    private static bool Take(string[] args, ref int index, string option, out string value, out string? error)
    {
        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            error = $"选项 {option} 缺少参数值。";
            return false;
        }

        value = args[++index];
        error = null;
        return true;
    }

    private static bool TakeNonNegativeInt(string[] args, ref int index, string option, out int value, out string? error)
    {
        value = 0;
        if (!Take(args, ref index, option, out var text, out error) || !int.TryParse(text, out value) || value < 0)
        {
            error ??= $"选项 {option} 需要非负整数。";
            return false;
        }

        return true;
    }

    private static bool TakePositiveInt(string[] args, ref int index, string option, out int value, out string? error)
    {
        value = 0;
        if (!Take(args, ref index, option, out var text, out error) || !int.TryParse(text, out value) || value <= 0)
        {
            error ??= $"选项 {option} 需要正整数。";
            return false;
        }

        return true;
    }

    private static bool TakePositiveLong(string[] args, ref int index, string option, out long value, out string? error)
    {
        value = 0;
        if (!Take(args, ref index, option, out var text, out error) || !long.TryParse(text, out value) || value <= 0)
        {
            error ??= $"选项 {option} 需要正整数。";
            return false;
        }

        return true;
    }

    private static bool TryParseEncoding(string value, out LogTextEncoding encoding)
    {
        switch (value.ToLowerInvariant())
        {
            case "utf8":
            case "utf-8":
                encoding = LogTextEncoding.Utf8;
                return true;
            case "gbk":
            case "gb2312":
            case "gb18030":
                encoding = LogTextEncoding.Gbk;
                return true;
            default:
                encoding = LogTextEncoding.Utf8;
                return false;
        }
    }
}
