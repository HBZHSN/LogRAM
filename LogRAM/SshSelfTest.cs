using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LogRAM;

internal static class SshSelfTest
{
    internal static async Task<int> RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LogRAM-ssh-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var config = Path.Combine(directory, "config");
            File.WriteAllText(config, "# Host commented\nHost alpha beta *.wild !excluded # ignored\nInclude \"extra *.conf\"\nHost=\"quoted\"\n");
            File.WriteAllText(Path.Combine(directory, "extra hosts.conf"), "Host gamma\nInclude config\n");
            Check(SshConfig.ReadHosts(config).SequenceEqual(new[] { "alpha", "beta", "gamma", "quoted" }), "SSH config aliases, comments, wildcard Include and cycles");
            try
            {
                await SshSession.ConnectAsync(config, "-oProxyCommand=invalid", default);
                throw new Exception("Option-like SSH host accepted");
            }
            catch (ArgumentException) { }

            var start = new ProcessStartInfo("python");
            foreach (var argument in new[] { "-u", "-c", SshSession.Bootstrap }) start.ArgumentList.Add(argument);
            using var session = await SshSession.StartAsync(start, default);
            var path = Path.Combine(directory, "日志 ' $test.log");
            var content = "INFO\r\nERROR first\nerror retry\n中文错误\nlast";
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(true));
            var listing = await session.RequestAsync("list", new { path = directory });
            Check(listing.GetProperty("entries").EnumerateArray().Any(entry => entry.GetProperty("name").GetString() == Path.GetFileName(path)), "remote file listing and Unicode paths");
            using var remote = new SshLogDocument(new SshTarget(config, "test", path), session);
            await remote.OpenAsync(LogTextEncoding.Utf8, default);
            await remote.RefreshMemoryStatusAsync();
            Check(remote.MemoryStatus is { ProcessBytes: > 0, AvailableBytes: >= 0 } && remote.MemoryUsage > 0, "actual remote worker and available memory");
            ILogDocument document = remote;
            var page = await document.ReadPageAsync(2, 2, default);
            Check(document.LineCount == 5 && page.Lines.Select(line => line.Text).SequenceEqual(new[] { "ERROR first", "error retry" }), "remote metadata and bounded page decoding");
            var matches = new List<LogSearchResult>();
            var summary = await document.SearchAsync("error", false, false, matches.AddRange, null, default);
            Check(summary.MatchCount == 2 && matches[0].Text == "ERROR first", "remote plain search and hydrated results");
            matches.Clear();
            summary = await document.AdvancedSearchAsync(new[] { "error" }, new[] { "retry" }, false, matches.AddRange, null, default);
            Check(summary.MatchCount == 1 && matches.Single().LineNumber == 2, "remote advanced search");
            await File.AppendAllTextAsync(path, " error\nERROR new\n");
            var appended = await document.RefreshAsync();
            Check(appended.OldLineCount == 5 && appended.NewLineCount == 6 && !appended.IsTruncated, "incremental remote indexing");
            matches.Clear();
            await document.AdvancedSearchLinesAsync(5, new[] { "error" }, new[] { "retry" }, false, matches.AddRange, default);
            Check(remote.MatchCount == 3 && matches.Select(item => item.LineNumber).SequenceEqual(new long[] { 5, 6 }), "live match count and partial last line");
            try { await document.SearchAsync("[", true, false, _ => { }, null, default); throw new Exception("Invalid regex accepted"); }
            catch (IOException) { }
            Check((await document.ReadPageAsync(6, 1, default)).Lines[0].Text == "ERROR new", "protocol remains usable after a remote error");

            var largePath = Path.Combine(directory, "large.log");
            await File.WriteAllTextAsync(largePath, string.Concat(Enumerable.Repeat("ERROR repeated\n", 300000)));
            var large = new SshLogDocument(new SshTarget(config, "test", largePath), session);
            await large.OpenAsync(LogTextEncoding.Utf8, default);
            matches.Clear();
            summary = await large.SearchAsync("error", false, false, matches.AddRange, null, default);
            Check(summary.MatchCount == 300000 && matches.Count == 1000, "remote results stay bounded while the full count is retained");
            using var cancel = new CancellationTokenSource();
            try
            {
                await large.SearchAsync("error", false, false, _ => { }, new InlineProgress(_ => cancel.Cancel()), cancel.Token);
                throw new Exception("Cancellation ignored");
            }
            catch (OperationCanceledException) { }
            Check((await large.ReadPageAsync(200000, 1, default)).Lines[0].LineNumber == 200000, "cancellation drains its response before the next request");
            Check((await File.ReadAllTextAsync(path)).StartsWith(content, StringComparison.Ordinal), "source log remains intact");

            var partialPath = Path.Combine(directory, "partial.log");
            await File.WriteAllTextAsync(partialPath, "ERROR");
            var partial = new SshLogDocument(new SshTarget(config, "test", partialPath), session);
            await partial.OpenAsync(LogTextEncoding.Utf8, default);
            await partial.SearchAsync("^ERROR$", true, true, _ => { }, null, default);
            await File.AppendAllTextAsync(partialPath, " changed");
            await partial.RefreshAsync();
            await partial.SearchLinesAsync(1, "^ERROR$", true, true, _ => { }, default);
            Check(partial.MatchCount == 0 && partial.ReturnedCount == 0, "appending to the last line can remove a regex match");

            using ILogDocument local = LogFileDocument.Open(path, null);
            Check((await local.ReadPageAsync(2, 1, default)).Lines[0].Text == "ERROR first", "local document adapter regression");
            if (Environment.GetEnvironmentVariable("LOGRAM_SSH_TEST_CONFIG") is { } liveConfig)
            {
                var target = new SshTarget(liveConfig, Environment.GetEnvironmentVariable("LOGRAM_SSH_TEST_HOST")!, Environment.GetEnvironmentVariable("LOGRAM_SSH_TEST_PATH")!);
                using (var browseSession = await SshSession.ConnectAsync(liveConfig, target.Host, default))
                {
                    var prefixPath = target.Path[..target.Path.LastIndexOf('/')] + "/sam";
                    var entries = (await browseSession.RequestAsync("list", new { path = prefixPath, filter = true })).GetProperty("entries");
                    Check(entries.GetArrayLength() == 2 && entries.EnumerateArray().All(entry => entry.GetProperty("name").GetString()!.StartsWith("sam", StringComparison.Ordinal)), "real SSH prefix filtering");
                }
                using var live = await SshLogDocument.OpenAsync(target, LogTextEncoding.Utf8);
                await live.RefreshMemoryStatusAsync();
                Check(live.MemoryStatus is { ProcessBytes: > 0, AvailableBytes: >= 0 }, "real Linux memory statistics");
                Check(live.FileSize > 5_000_000 && live.LineCount == 600003, "real SSH remote file metadata");
                Check((await live.ReadPageAsync(600003, 1, default)).Lines.Single().Text == "ERROR last", "real SSH last-page read");
                matches.Clear();
                Check((await live.SearchAsync("error", false, false, matches.AddRange, null, default)).MatchCount == 2 && matches.Count == 2, "real SSH remote search");
                Console.WriteLine("PASS: real OpenSSH connection, remote Linux Python worker, metadata, paging and search (7.2 MB remote fixture).");
            }
            Console.WriteLine("PASS: SSH config, worker bootstrap, listing, paging, search, live refresh, limits, cancellation, error recovery and local document regression.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }

    private sealed class InlineProgress(Action<LogSearchProgress> report) : IProgress<LogSearchProgress>
    {
        public void Report(LogSearchProgress value) => report(value);
    }
}
