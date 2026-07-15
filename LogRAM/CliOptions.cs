using System;
using System.Collections.Generic;

namespace LogRAM;

internal sealed class CliOptions
{
    public string FilePath { get; private set; } = string.Empty;

    public string Keyword { get; private set; } = string.Empty;

    public string? OutputPath { get; private set; }

    public bool UseRegex { get; private set; }

    public bool CaseSensitive { get; private set; }

    public bool ShowLineNumber { get; private set; }

    public LogTextEncoding Encoding { get; private set; } = LogTextEncoding.Utf8;

    public static bool LooksLikeCli(string[] args)
    {
        var positionals = 0;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-s":
                case "--search":
                    return true;
                case "-h":
                case "--help":
                    return true;
                case "-o":
                case "--output":
                case "-e":
                case "--encoding":
                    i++;
                    break;
                case "-r":
                case "--regex":
                case "-c":
                case "--case-sensitive":
                case "-n":
                case "--line-number":
                    break;
                default:
                    if (!arg.StartsWith('-'))
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

        string? keyword = null;
        var positionals = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-o":
                case "--output":
                    if (!TryTakeValue(args, ref i, out var outputValue))
                    {
                        error = $"选项 {arg} 缺少参数值。";
                        return false;
                    }

                    options.OutputPath = outputValue;
                    break;
                case "-s":
                case "--search":
                    if (!TryTakeValue(args, ref i, out var searchValue))
                    {
                        error = $"选项 {arg} 缺少参数值。";
                        return false;
                    }

                    keyword = searchValue;
                    break;
                case "-e":
                case "--encoding":
                    if (!TryTakeValue(args, ref i, out var encodingValue))
                    {
                        error = $"选项 {arg} 缺少参数值。";
                        return false;
                    }

                    if (!TryParseEncoding(encodingValue, out var encoding))
                    {
                        error = $"不支持的编码：{encodingValue}（可选 utf8 或 gbk）。";
                        return false;
                    }

                    options.Encoding = encoding;
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
                case "-h":
                case "--help":
                    error = null;
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

        options.FilePath = positionals[0];

        keyword ??= positionals.Count > 1 ? positionals[1] : null;
        if (string.IsNullOrEmpty(keyword))
        {
            error = "缺少搜索关键词参数。";
            return false;
        }

        options.Keyword = keyword;
        return true;
    }

    private static bool TryTakeValue(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            return false;
        }

        value = args[++index];
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
