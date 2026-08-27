using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LogRAM;

public static class CliRunner
{
    private const int AttachParentProcess = -1;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    public static bool IsCliInvocation(string[] args) => CliOptions.LooksLikeCli(args);

    public static async Task<int> RunAsync(string[] args)
    {
        AttachToConsole();

        if (!CliOptions.TryParse(args, out var options, out var error))
        {
            if (error is not null)
            {
                Console.Error.WriteLine(error);
            }

            WriteUsage();
            return error is null ? 0 : 1;
        }

        var filePath = Path.GetFullPath(options.FilePath);
        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"错误：文件不存在：{filePath}");
            return 1;
        }

        LogFileDocument? document = null;
        TextWriter? fileWriter = null;
        try
        {
            document = LogFileDocument.Open(filePath, options.Encoding);
            var output = options.OutputPath is null
                ? Console.Out
                : fileWriter = new StreamWriter(options.OutputPath, append: false, new UTF8Encoding(false));

            var count = await SearchToWriterAsync(document, options, output, CancellationToken.None);
            if (options.CountOnly)
            {
                output.WriteLine(count);
            }

            await output.FlushAsync();
            Console.Error.WriteLine($"匹配 {count} 行。{(count > options.MaxCount ? $" 已输出前 {options.MaxCount} 行。" : string.Empty)}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误：{ex.Message}");
            return 1;
        }
        finally
        {
            fileWriter?.Dispose();
            document?.Dispose();
        }
    }

    private static async Task<long> SearchToWriterAsync(
        LogFileDocument document,
        CliOptions options,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var query = BuildQuery(options);
        var collect = options.Json || options.Before > 0 || options.After > 0;
        var matchLines = collect ? new List<long>() : null;
        var writeLock = new object();
        var returned = 0;

        void OnMatch(LogSearchResult result)
        {
            lock (writeLock)
            {
                if (returned >= options.MaxCount)
                {
                    return;
                }

                returned++;
                if (matchLines is not null)
                {
                    matchLines.Add(result.LineNumber);
                }
                else if (!options.CountOnly)
                {
                    WritePlainLine(output, result.LineNumber, result.Text, options.ShowLineNumber);
                }
            }
        }

        var count = await LogQueryEngine.SearchAsync(document, query, OnMatch, cancellationToken);
        if (matchLines is null || options.CountOnly)
        {
            return count;
        }

        var lines = LogQueryEngine.ReadContext(document, matchLines, options.Before, options.After);
        if (options.Json)
        {
            output.WriteLine(JsonSerializer.Serialize(new
            {
                path = document.FilePath,
                encoding = document.EncodingKind.ToString().ToLowerInvariant(),
                fileSize = document.FileSize,
                lineCount = document.LineCount,
                matchCount = count,
                returnedMatchCount = matchLines.Count,
                truncated = count > matchLines.Count,
                lines
            }, JsonOptions));
            return count;
        }

        long previous = 0;
        foreach (var line in lines)
        {
            if (previous > 0 && line.LineNumber > previous + 1)
            {
                output.WriteLine("--");
            }

            output.Write(line.LineNumber);
            output.Write(line.IsMatch ? ':' : '-');
            output.WriteLine(line.Text);
            previous = line.LineNumber;
        }

        return count;
    }

    private static LogQuery BuildQuery(CliOptions options)
    {
        var pattern = options.Pattern;
        IReadOnlyList<string> any = options.IncludeAny;
        IReadOnlyList<string> excludes = options.Exclude;

        if (!options.UseRegex && options.IncludeAny.Count == 0 && options.IncludeAll.Count == 0 && options.Exclude.Count == 0 &&
            AdvancedSearchQuery.TryParse(pattern, out var advanced))
        {
            pattern = null;
            any = advanced.Includes;
            excludes = advanced.Excludes;
        }

        return new LogQuery
        {
            Pattern = pattern,
            IncludeAny = any,
            IncludeAll = options.IncludeAll,
            Exclude = excludes,
            UseRegex = options.UseRegex,
            CaseSensitive = options.CaseSensitive,
            StartLine = options.StartLine,
            EndLine = options.EndLine
        };
    }

    private static void WritePlainLine(TextWriter output, long lineNumber, string text, bool showLineNumber)
    {
        if (showLineNumber)
        {
            output.Write(lineNumber);
            output.Write(':');
        }

        output.WriteLine(text);
    }

    private static void AttachToConsole()
    {
        AttachConsole(AttachParentProcess);
        var encoding = Console.OutputEncoding.CodePage == Encoding.UTF8.CodePage
            ? new UTF8Encoding(false)
            : Console.OutputEncoding;
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), encoding) { AutoFlush = true });
    }

    private static void WriteUsage()
    {
        Console.Out.WriteLine(
            "用法：LogRAM-cli <文件路径> [搜索关键词] [选项]\n" +
            "\n" +
            "查询：\n" +
            "  -s, --search <关键词>       单个文本/正则条件\n" +
            "  -i, --any <关键词>          命中任一，可重复（OR）\n" +
            "  -a, --all <关键词>          必须全部命中，可重复（AND）\n" +
            "  -x, --exclude <关键词>      排除命中行，可重复（NOT）\n" +
            "  -r, --regex                 将 --search 作为正则表达式\n" +
            "  -c, --case-sensitive        区分大小写\n" +
            "      --start-line <行号>     查询起始行\n" +
            "      --end-line <行号>       查询结束行\n" +
            "\n" +
            "输出：\n" +
            "  -B, --before <数量>         命中前上下文\n" +
            "  -A, --after <数量>          命中后上下文\n" +
            "  -C, --context <数量>        命中前后上下文\n" +
            "  -m, --max-count <数量>      最多输出的命中数\n" +
            "  -n, --line-number           输出行号\n" +
            "      --json                  输出适合 AI/脚本读取的 JSON\n" +
            "      --count-only            只输出命中总数\n" +
            "  -o, --output <路径>         将结果写入文件\n" +
            "  -e, --encoding <编码>       utf8 或 gbk（默认 utf8）\n" +
            "  -h, --help                  显示帮助\n" +
            "\n" +
            "兼容高级语法：\"in(a,b);notin(c)\"");
    }
}
