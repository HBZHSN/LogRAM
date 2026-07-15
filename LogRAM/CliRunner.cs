using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LogRAM;

public static class CliRunner
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    public static bool IsCliInvocation(string[] args)
    {
        return CliOptions.LooksLikeCli(args);
    }

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

        var filePath = options.FilePath;
        if (!Path.IsPathFullyQualified(filePath))
        {
            filePath = Path.GetFullPath(filePath);
        }

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

            TextWriter output;
            if (options.OutputPath is not null)
            {
                fileWriter = new StreamWriter(options.OutputPath, append: false, new UTF8Encoding(false));
                output = fileWriter;
            }
            else
            {
                output = Console.Out;
            }

            var count = await SearchToWriterAsync(document, options, output, CancellationToken.None);

            await output.FlushAsync();
            Console.Error.WriteLine($"匹配 {count} 行。");
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
        var writeLock = new object();

        void WriteBatch(IReadOnlyList<LogSearchResult> batch)
        {
            lock (writeLock)
            {
                foreach (var result in batch)
                {
                    if (options.ShowLineNumber)
                    {
                        output.Write(result.LineNumber);
                        output.Write(':');
                    }

                    output.WriteLine(result.Text);
                }
            }
        }

        if (AdvancedSearchQuery.TryParse(options.Keyword, out var advanced))
        {
            var badTerm = advanced.Includes
                .Concat(advanced.Excludes)
                .FirstOrDefault(term => !IsAsciiKeyword(term));
            if (badTerm is not null)
            {
                throw new ArgumentException("高级搜索关键词仅支持 ASCII 字符。");
            }

            var advancedSummary = await document.AdvancedSearchAsync(
                advanced.Includes,
                advanced.Excludes,
                options.CaseSensitive,
                WriteBatch,
                progress: null,
                cancellationToken);
            return advancedSummary.MatchCount;
        }

        var summary = await document.SearchAsync(
            options.Keyword,
            options.UseRegex,
            options.CaseSensitive,
            WriteBatch,
            progress: null,
            cancellationToken);
        return summary.MatchCount;
    }

    private static bool IsAsciiKeyword(string term)
    {
        foreach (var ch in term)
        {
            if (ch > '\x7F')
            {
                return false;
            }
        }

        return true;
    }

    private static void AttachToConsole()
    {
        AttachConsole(AttachParentProcess);
        var encoding = Console.OutputEncoding;
        if (encoding.CodePage == Encoding.UTF8.CodePage)
        {
            encoding = new UTF8Encoding(false);
        }

        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), encoding) { AutoFlush = true });
    }

    private static void WriteUsage()
    {
        Console.Out.WriteLine(
            "用法：LogRAM <文件路径> <搜索关键词> [选项]\n" +
            "\n" +
            "选项：\n" +
            "  -o, --output <路径>     将结果写入文件（默认输出到管道/标准输出）\n" +
            "  -s, --search <关键词>   指定搜索关键词（等同于第二个位置参数）\n" +
            "  -r, --regex             以正则表达式方式搜索\n" +
            "  -c, --case-sensitive    区分大小写\n" +
            "  -n, --line-number       在每行前输出行号\n" +
            "  -e, --encoding <编码>   文件编码：utf8 或 gbk（默认 utf8）\n" +
            "  -h, --help              显示帮助\n" +
            "\n" +
            "关键词支持高级语法，例如：\"in(a,b);not in(c)\"");
    }
}
