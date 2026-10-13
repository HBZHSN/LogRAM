using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace LogRAM.Npp;

internal static class SelfTest
{
    internal static async Task<int> RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LogRAM-npp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "中文 space.log");
            await File.WriteAllTextAsync(path, "info start\r\nERROR first\r\nwarning retry\r\n中文错误\r\nerror tail", new UTF8Encoding(false));
            using var session = new LogSession(LogFileDocument.Open(path, LogTextEncoding.Utf8));
            Check(session.Document.LineCount == 5, "CRLF / partial final line");
            Check(await session.SearchAsync("error", false, false, default) == 2 && session.MatchLine(1) == 5, "ASCII ignore case");
            Check(await session.SearchAsync("error", false, true, default) == 1, "case sensitive");
            Check(await session.SearchAsync("in(error,warning);notin(retry)", false, false, default) == 2, "advanced include / exclude");
            Check(await session.SearchAsync("中文", false, false, default) == 1 && session.MatchLine(0) == 4, "Unicode");
            Check(await session.SearchAsync("^(ERROR|warning)", true, true, default) == 2, "regex");
            try { await session.SearchAsync("[", true, false, default); throw new Exception("Invalid regex accepted"); }
            catch (ArgumentException) { }
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try { await session.SearchAsync("error", false, false, cancellation.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { }
            try { LogFileDocument.Open(path, LogTextEncoding.Utf8, null, cancellation.Token); throw new Exception("Load cancellation ignored"); }
            catch (OperationCanceledException) { }
            var progress = new InlineProgress();
            Check(await session.SearchAsync("error", false, false, default, progress) == 2 && progress.Bytes == session.Document.FileSize, "progress forwarding");
            await File.AppendAllTextAsync(path, " retry\nerror appended\n", new UTF8Encoding(false));
            await session.SearchAsync("in(error);notin(retry)", false, false, default);
            var appended = await session.RefreshAsync(default);
            Check(appended.HasNewContent && session.MatchCount == 2 && session.MatchLine(0) == 2 && session.MatchLine(1) == 6,
                "incremental tail removes stale last-line match");
            var export = Path.Combine(directory, "export.txt");
            await session.ExportAsync(export, default);
            var text = await File.ReadAllTextAsync(export);
            Check(text.Contains("2\tERROR first") && text.Contains("6\terror appended"), "export all results");
            try { await session.ExportAsync(path, default); throw new Exception("Source overwrite allowed"); }
            catch (IOException) { }
            await File.WriteAllTextAsync(export, "keep me");
            try { await session.ExportAsync(export, cancellation.Token); throw new Exception("Export cancellation ignored"); }
            catch (OperationCanceledException) { }
            Check(await File.ReadAllTextAsync(export) == "keep me" && Directory.GetFiles(directory, "*.tmp").Length == 0, "atomic cancelled export");
            using var empty = new LogSession(LogFileDocument.Open(CreateFile(directory, "empty.log", ""), LogTextEncoding.Utf8));
            Check(empty.Document.LineCount == 0 && await empty.SearchAsync("error", false, false, default) == 0, "empty log");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var gbkPath = Path.Combine(directory, "gbk.log");
            File.WriteAllText(gbkPath, "信息\n中文错误\n", Encoding.GetEncoding(936));
            using var gbk = new LogSession(LogFileDocument.Open(gbkPath, LogTextEncoding.Gbk));
            Check(await gbk.SearchAsync("错误", false, true, default) == 1 && gbk.Document.GetLineText(2) == "中文错误", "GBK");
            var settingsPath = Path.Combine(directory, "settings.json");
            var settings = ViewerSettings.Load(settingsPath);
            ViewerSettings.Remember(settings.RecentFiles, path);
            settings.Save(settingsPath);
            Check(ViewerSettings.Load(settingsPath).RecentFiles.Single() == path, "persistent settings");
            File.WriteAllText(settingsPath, "{\"RecentFiles\":null,\"SearchHistory\":null}");
            Check(ViewerSettings.Load(settingsPath).RecentFiles.Count == 0, "malformed settings normalization");
            File.WriteAllText(settingsPath, "{\"Language\":null,\"RecentFiles\":[null,\"\",\"valid.log\"],\"SearchHistory\":[null,\"  \",\"error\"]}");
            var normalized = ViewerSettings.Load(settingsPath);
            Check(normalized.Language == "zh" && normalized.RecentFiles.SequenceEqual(new[] { "valid.log" }) &&
                normalized.SearchHistory.SequenceEqual(new[] { "error" }), "null settings entries");
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => settings.Save(settingsPath))));
            Check(ViewerSettings.Load(settingsPath).RecentFiles.Single() == path && Directory.GetFiles(directory, "*.tmp").Length == 0,
                "concurrent settings save and cleanup");
            await File.WriteAllTextAsync(path, "short");
            Check((await session.RefreshAsync(default)).IsTruncated, "truncation detection");
            Console.WriteLine("PASS: search, Unicode/GBK, progress, cancellation, incremental tail, safe export, settings and truncation.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(directory, recursive: true); }
    }

    internal static int RunUi()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LogRAM-npp-ui-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var parent = new ShortcutReceiver();
            using var viewer = new ViewerForm(parent.Handle, Environment.ProcessId, directory);
            var first = CreateFile(directory, "first.log", "ERROR one\ninfo\nerror two\n");
            var second = CreateFile(directory, "second.log", "中文\nwarning\n");
            Pump(viewer.OpenFileAsync(first));
            var query = Field<ToolStripComboBox>(viewer, "_query");
            query.Text = "error";
            Pump((Task)Call(viewer, "SearchAsync")!);
            Check(Field<LogSession>(viewer, "_current").MatchCount == 2 && query.Text == "error", "UI search and history retain query");
            Pump(viewer.OpenFileAsync(second));
            query.Text = "中文";
            Pump((Task)Call(viewer, "SearchAsync")!);
            Call(viewer, "SelectSession", 0);
            Check(query.Text == "error" && Field<LogSession>(viewer, "_current").MatchCount == 2, "switch back retains first query and results");
            Call(viewer, "SelectSession", 1);
            Check(query.Text == "中文" && Field<LogSession>(viewer, "_current").MatchCount == 1, "second query and results remain isolated");
            Pump(viewer.OpenFileAsync(first));
            Check(Field<List<LogSession>>(viewer, "_sessions").Count == 2 && Field<LogSession>(viewer, "_current").Document.FilePath == first,
                "duplicate file selects existing session");
            var split = Field<SplitContainer>(viewer, "_split");
            var bar = Field<ToolStrip>(viewer, "_queryBar");
            viewer.PerformLayout();
            Check(bar.Parent == split.Panel2 && bar.Dock == DockStyle.Top &&
                Field<DataGridView>(viewer, "_source").Parent == split.Panel1 &&
                Field<DataGridView>(viewer, "_results").Top >= bar.Bottom, "search toolbar is above the lower result pane");
            Check(viewer.Controls.OfType<ToolStrip>().All(item => item is StatusStrip) &&
                bar.Items.Cast<ToolStripItem>().All(item => item.Placement == ToolStripItemPlacement.Main), "one toolbar row with no top file toolbar");
            Check(bar.Items.OfType<ToolStripSeparator>().Count() == 4, "toolbar has five action groups");
            foreach (var dark in new[] { true, false })
            {
                Call(viewer, "ApplyTheme", dark);
                var icons = bar.Items.OfType<ToolStripButton>().Where(item => item.DisplayStyle == ToolStripItemDisplayStyle.Image).ToArray();
                Check(icons.Length == 8 && icons.All(item => item.Image is not null && !string.IsNullOrWhiteSpace(item.AccessibleName) &&
                    !string.IsNullOrWhiteSpace(item.ToolTipText)), "icon actions retain accessible names and tips in both themes");
                foreach (var button in icons)
                {
                    var bitmap = (Bitmap)button.Image!;
                    Check(bitmap.GetPixel(0, 0).A == 0 && Enumerable.Range(0, bitmap.Width).Any(x =>
                        Enumerable.Range(0, bitmap.Height).Any(y => bitmap.GetPixel(x, y).A > 0)), "transparent icon contains visible strokes");
                }
            }
            Check(Field<ToolStripButton>(viewer, "_regex").Text == ".*" && Field<ToolStripButton>(viewer, "_case").Text == "Aa",
                "search option symbols match original LOGRAM");
            Pump(viewer.OpenFileAsync(first, LogTextEncoding.Gbk));
            Check(Field<LogSession>(viewer, "_current").Document.EncodingKind == LogTextEncoding.Gbk, "host encoding changes cached document");
            Pump(viewer.OpenFileAsync(first, LogTextEncoding.Utf8));
            Check(Field<LogSession>(viewer, "_current").Document.EncodingKind == LogTextEncoding.Utf8 && query.Text == "error", "encoding switch preserves query");
            var message = new Message();
            Check((bool)Call(viewer, "ProcessCmdKey", message, Keys.Control | Keys.O)!, "open shortcut is handled");
            Application.DoEvents();
            Check(parent.HostCommand == 41002, "open shortcut delegates to NPP");
            Check((bool)Call(viewer, "ProcessCmdKey", message, Keys.Control | Keys.W)!, "close shortcut is handled");
            Application.DoEvents();
            Check(parent.HostCommand == 41003, "close shortcut delegates to NPP");
            Check((bool)Call(viewer, "ProcessCmdKey", message, Keys.Control | Keys.Alt | Keys.L)!, "workspace shortcut is handled inside viewer");
            Application.DoEvents();
            Check(parent.ReturnRequested, "workspace shortcut requests return to NPP");
            parent.ReturnRequested = false;
            typeof(ViewerForm).GetField("_workspaceShortcut", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewer, Keys.Control | Keys.Alt | Keys.M);
            Check(!(bool)Call(viewer, "ProcessCmdKey", message, Keys.Control | Keys.Alt | Keys.L)!, "old shortcut no longer toggles after remapping");
            Check((bool)Call(viewer, "ProcessCmdKey", message, Keys.Control | Keys.Alt | Keys.M)!, "remapped shortcut is handled");
            Application.DoEvents();
            Check(parent.ReturnRequested, "remapped shortcut requests return to NPP");
            Console.WriteLine("PASS: two-pane toolbar layout, host file shortcuts/encoding, UI search/history, isolated multi-file state and workspace shortcut.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static object? Call(object owner, string name, params object[] arguments) =>
        owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, arguments);
    private static void Pump(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(10)) { Application.DoEvents(); Thread.Sleep(1); }
        Check(task.IsCompleted, "UI operation timed out");
        task.GetAwaiter().GetResult();
    }
    private sealed class ShortcutReceiver : Form
    {
        internal bool ReturnRequested;
        internal int HostCommand;
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == Native.HideMessage) ReturnRequested = true;
            if (message.Msg == Native.CommandMessage) HostCommand = message.WParam.ToInt32();
            base.WndProc(ref message);
        }
    }

    internal static async Task<int> BenchmarkAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "LogRAM-npp-benchmark-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            var block = Enumerable.Repeat((byte)'x', 256 * 1024).ToArray();
            for (var i = 255; i < block.Length; i += 256) block[i] = (byte)'\n';
            Encoding.ASCII.GetBytes("ERROR ").CopyTo(block, 0);
            await using (var stream = File.Create(path))
                for (var i = 0; i < 1088; i++) await stream.WriteAsync(block);
            var watch = Stopwatch.StartNew();
            using var session = new LogSession(LogFileDocument.Open(path, LogTextEncoding.Utf8));
            var load = watch.Elapsed.TotalSeconds;
            watch.Restart();
            var matches = await session.SearchAsync("error", false, false, default);
            Check(matches == 1088 && session.Document.LineCount == 1088 * 1024L, "multichunk parallel search");
            Console.WriteLine($"PASS: 272 MiB, {session.Document.LineCount:N0} lines, {matches} matches; load {load:F3}s, search {watch.Elapsed.TotalSeconds:F3}s.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static string CreateFile(string directory, string name, string text)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, text);
        return path;
    }
    internal static async Task<int> RealFileBenchmarkAsync(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
            var before = new FileInfo(path);
            var length = before.Length;
            var modified = before.LastWriteTimeUtc;
            var watch = Stopwatch.StartNew();
            using var session = new LogSession(LogFileDocument.Open(path, LogTextEncoding.Utf8));
            var load = watch.Elapsed.TotalSeconds;
            var queries = new[] { "error", "SendOrder" };
            var counts = new long[queries.Length];
            for (var i = 0; i < queries.Length; i++)
            {
                watch.Restart();
                counts[i] = await session.SearchAsync(queries[i], false, false, default);
                Console.WriteLine($"Search {queries[i]}: {counts[i]:N0} matches, {watch.Elapsed.TotalSeconds:F3}s.");
            }
            var expected = new long[queries.Length];
            long lines = 0;
            watch.Restart();
            using (var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8))
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    lines++;
                    for (var i = 0; i < queries.Length; i++) if (line.Contains(queries[i], StringComparison.OrdinalIgnoreCase)) expected[i]++;
                }
            }
            Check(lines == session.Document.LineCount && expected.SequenceEqual(counts), "independent streaming baseline mismatch");
            var after = new FileInfo(path);
            Check(after.Length == length && after.LastWriteTimeUtc == modified, "source log changed during test");
            Console.WriteLine($"PASS: {length:N0} bytes, {lines:N0} lines; load {load:F3}s; independent baseline {watch.Elapsed.TotalSeconds:F3}s; indexed memory {session.Document.MemoryUsage / 1048576.0:N1} MiB. Source unchanged.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class InlineProgress : IProgress<LogSearchProgress>
    {
        internal long Bytes;
        public void Report(LogSearchProgress value) => Bytes = value.BytesRead;
    }
}
