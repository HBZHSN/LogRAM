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
            // Regression tests for case-insensitive advanced search with pure numeric / symbol / long keywords
            var advPath = Path.Combine(directory, "adv-case.log");
            var longPrefix = new string('A', 40);
            var sb = new StringBuilder();
            sb.AppendLine("INFO 100 boot");
            sb.AppendLine("ERROR 404 not found");
            sb.AppendLine("warn 200 retry");
            sb.AppendLine("error 404 timeout");
            sb.AppendLine("INFO 500 " + longPrefix.ToLowerInvariant() + "_9999");
            for (var i = 0; i < 6000; i++) sb.AppendLine("filler line padding abcdefghijklmnopqrstuvwxyz abcdefghijklmnopqrstuvwxyz");
            sb.AppendLine("ERROR 404 tail_hit");
            sb.AppendLine("ERROR 200 excluded_tail");
            await File.WriteAllTextAsync(advPath, sb.ToString(), new UTF8Encoding(false));
            using (var advSession = new LogSession(LogFileDocument.Open(advPath, LogTextEncoding.Utf8)))
            {
                Check(await advSession.SearchAsync("in(404)", false, false, default) == 3 &&
                    advSession.MatchLine(0) == 2 && advSession.MatchLine(1) == 4 && advSession.MatchLine(2) == 6006,
                    "advanced case-insensitive pure numeric include across >256KB");
                Check(await advSession.SearchAsync("in(404)", false, true, default) == 3, "advanced case-sensitive pure numeric include");
                Check(await advSession.SearchAsync("in(error,500);notin(200)", false, false, default) == 4,
                    "advanced case-insensitive mixed letter+numeric include with numeric exclude");
                Check(await advSession.SearchAsync("in(ERROR);notin(200)", false, true, default) == 2,
                    "advanced case-sensitive letter include with numeric exclude");
                Check(await advSession.SearchAsync("in(error);notin(RETRY)", false, false, default) == 4,
                    "advanced case-insensitive letter exclude");
                Check(await advSession.SearchAsync($"in({longPrefix}_9999)", false, false, default) == 1 && advSession.MatchLine(0) == 5,
                    "advanced case-insensitive long (>=32B) pattern");
                Check(await advSession.SearchAsync("notin(filler,200,100,404)", false, false, default) == 1 && advSession.MatchLine(0) == 5,
                    "advanced case-insensitive exclude-only with numeric terms");

                // Pinning & secondary search session tests
                Check(await advSession.SearchAsync("in(error,500)", false, false, default) == 5, "primary search before pin/secondary");
                Check(advSession.TogglePin(6006) && advSession.TogglePin(2), "toggle pin adds lines");
                Check(advSession.PinnedCount == 2 && advSession.PinnedAt(0).LineNumber == 2 && advSession.PinnedAt(1).LineNumber == 6006,
                    "pinned lines sorted ascending by line number");
                Check(advSession.UpdateResultFindMatches("TIMEOUT", caseSensitive: false) == 1 &&
                    advSession.MatchLine(advSession.ResultFindMatchAt(0)) == 4, "secondary find case-insensitive");
                Check(advSession.UpdateResultFindMatches("TIMEOUT", caseSensitive: true) == 0, "secondary find case-sensitive");
                Check(advSession.UpdateResultFindMatches("404", caseSensitive: false) == 3, "secondary find multi-match");
                Check(advSession.CommitSecondarySearchAll() == 3 && advSession.SecondaryCount == 3 &&
                    advSession.ActiveSideTab == SidePaneTab.SecondarySearch, "commit all populates secondary tab");
                Check(await advSession.SearchAsync("boot", false, false, default) == 1, "new search runs cleanly");
                Check(advSession.PinnedCount == 2 && advSession.PinnedAt(0).Text.Contains("ERROR 404"),
                    "pinned lines persist across new search");
                Check(advSession.SecondaryCount == 0 && advSession.ResultFindMatchCount == 0,
                    "new search clears secondary results and find matches");
                Check(!advSession.TogglePin(2) && advSession.PinnedCount == 1, "toggle pin removes existing line");
                advSession.ClearPinned();
                Check(advSession.PinnedCount == 0, "clear pinned empties list");
            }
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
            Check(await gbk.SearchAsync("错误", false, true, default) == 1 && gbk.GetSourceLineText(2) == "中文错误", "GBK");
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

            var sshLogPath = Path.Combine(directory, "ssh remote.log");
            await File.WriteAllTextAsync(sshLogPath, "INFO head\nERROR first\nwarning retry\n中文错误\nerror tail", new UTF8Encoding(false));
            using var worker = await StartSshWorkerAsync();
            var target = new SshTarget(SshConfig.DefaultPath, "prod-host", sshLogPath);
            Check(SshTarget.TryParseUri(target.DisplayPath, out var parsedTarget) && parsedTarget.Host == "prod-host" && parsedTarget.Path == sshLogPath, "SSH URI round-trip");
            using var remoteDoc = new SshLogDocument(target, worker);
            await remoteDoc.OpenAsync(LogTextEncoding.Utf8, default);
            using var remoteSession = new LogSession(remoteDoc);
            Check(!remoteSession.HasSourcePage(1, 5), "remote page starts uncached");
            await remoteSession.FetchSourcePageAsync(1, 50, default);
            Check(remoteSession.HasSourcePage(1, 5) && remoteSession.GetSourceLineText(4) == "中文错误", "remote source page fetch and cache");
            Check(await remoteSession.SearchAsync("in(error,warning);notin(retry)", false, false, default) == 2 &&
                remoteSession.MatchCount == 2 && remoteSession.GetResultLineText(0) == "ERROR first", "remote advanced search and hydrated results");
            Check(remoteSession.TogglePin(5) && remoteSession.TogglePin(2) && remoteSession.PinnedCount == 2 &&
                remoteSession.PinnedAt(0).LineNumber == 2 && remoteSession.PinnedAt(0).Text == "ERROR first", "remote pinning snapshot");
            Check(remoteSession.UpdateResultFindMatches("FIRST", caseSensitive: false) == 1 &&
                remoteSession.CommitSecondarySearchAll() == 1 && remoteSession.SecondaryAt(0).LineNumber == 2, "remote secondary search");
            await File.AppendAllTextAsync(sshLogPath, " retry\nERROR appended\n", new UTF8Encoding(false));
            var remoteAppended = await remoteSession.RefreshAsync(default);
            Check(remoteAppended.HasNewContent && !remoteSession.HasSourcePage(1, 6) &&
                remoteSession.MatchCount == 2 && remoteSession.TotalMatchCount == 2 && remoteSession.MatchLine(1) == 6, "remote live tail refresh");
            var remoteExport = Path.Combine(directory, "remote-export.txt");
            await remoteSession.ExportAsync(remoteExport, default);
            Check((await File.ReadAllTextAsync(remoteExport)).Contains("6\tERROR appended"), "remote export uses hydrated matches");
            Console.WriteLine("PASS: search, Unicode/GBK, SSH worker/session, progress, cancellation, incremental tail, safe export, settings and truncation.");
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
            var resultsSplit = Field<SplitContainer>(viewer, "_resultsSplit");
            var bar = Field<ToolStrip>(viewer, "_queryBar");
            viewer.PerformLayout();
            Check(bar.Parent == split.Panel2 && bar.Dock == DockStyle.Top &&
                Field<DataGridView>(viewer, "_source").Parent == split.Panel1 &&
                resultsSplit.Parent == split.Panel2 && resultsSplit.Top >= bar.Bottom &&
                Field<DataGridView>(viewer, "_results").Parent == resultsSplit.Panel1, "search toolbar is above the lower result pane");
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
            Check(Field<ToolStripButton>(viewer, "_regex").Text == ".*" && Field<ToolStripButton>(viewer, "_case").Text == "Aa" &&
                Field<ToolStripButton>(viewer, "_advanced").Enabled &&
                Field<ToolStripButton>(viewer, "_ssh").Text == "SSH" && Field<ToolStripButton>(viewer, "_ssh").Enabled,
                "search option symbols, advanced button and SSH button match original LOGRAM");

            // Test WinForms AdvancedSearchForm parsing & formatting
            using (var advForm = new AdvancedSearchForm("in(error,404);notin(200)", dark: true))
            {
                Call(advForm, "Submit");
                Check(advForm.QueryText == "in(error,404);notin(200)", "WinForms AdvancedSearchForm round-trips include/exclude query");
            }

            // Test pinning, Ctrl+F secondary search, All button and right-pane tab switching in ViewerForm
            Call(viewer, "SelectSession", 0);
            query.Text = "in(error,info)";
            Pump((Task)Call(viewer, "SearchAsync")!);
            Check(Field<LogSession>(viewer, "_current").MatchCount == 3, "UI advanced search returns 3 lines");
            Check(resultsSplit.Panel2Collapsed, "right pane starts collapsed");
            Call(viewer, "HandleResultsCellClick", new DataGridViewCellEventArgs(0, 2)); // pin line 3
            Call(viewer, "HandleResultsCellClick", new DataGridViewCellEventArgs(0, 0)); // pin line 1
            Check(!resultsSplit.Panel2Collapsed && Field<LogSession>(viewer, "_current").PinnedCount == 2 &&
                Field<LogSession>(viewer, "_current").PinnedAt(0).LineNumber == 1 &&
                Field<LogSession>(viewer, "_current").PinnedAt(1).LineNumber == 3, "clicking line numbers pins lines in ascending order");
            // Open Ctrl+F find bar inside results
            var findInput = Field<TextBox>(viewer, "_resultFindInput");
            var findCount = Field<Label>(viewer, "_resultFindCount");
            viewer.OpenResultFindBar();
            Check(Field<LogSession>(viewer, "_current").IsResultFindOpen, "result find bar opens");
            findInput.Text = "error";
            Check(findCount.Text == "1/2", "result find counts case-insensitive matches");
            viewer.NavigateResultFind(1);
            Check(findCount.Text == "2/2", "result find navigates next");
            viewer.NavigateResultFind(1);
            Check(findCount.Text == "1/2", "result find wraps around");
            viewer.CommitSecondarySearchAll();
            var pinnedTabBtn = Field<Button>(viewer, "_pinnedTabBtn");
            var secondaryTabBtn = Field<Button>(viewer, "_secondaryTabBtn");
            Check(pinnedTabBtn.Text.Contains("2") && secondaryTabBtn.Text.Contains("2") &&
                Field<LogSession>(viewer, "_current").ActiveSideTab == SidePaneTab.SecondarySearch &&
                Field<LogSession>(viewer, "_current").SecondaryCount == 2,
                "All button shows secondary tab alongside pinned tab");
            // Switch tabs and verify click-to-jump updates highlighted source line
            viewer.SelectSideTab(SidePaneTab.Pinned);
            Check(Field<LogSession>(viewer, "_current").ActiveSideTab == SidePaneTab.Pinned, "switching to pinned tab works");
            Call(viewer, "SideGrid_CellClick", Field<DataGridView>(viewer, "_sideGrid"), new DataGridViewCellEventArgs(1, 1));
            Check(Field<long>(viewer, "_highlightedSourceLine") == 3, "clicking pinned line jumps and highlights source");
            // Verify session switch isolates right pane & find bar
            Call(viewer, "SelectSession", 1);
            Check(resultsSplit.Panel2Collapsed && !Field<LogSession>(viewer, "_current").IsResultFindOpen, "session switch hides right pane and find bar for clean session");
            Call(viewer, "SelectSession", 0);
            Check(!resultsSplit.Panel2Collapsed && Field<LogSession>(viewer, "_current").IsResultFindOpen, "switching back restores right pane and find bar");
            viewer.CloseResultFindBar();
            Check(!Field<LogSession>(viewer, "_current").IsResultFindOpen && !resultsSplit.Panel2Collapsed, "closing find bar keeps right pane tabs");
            // Clear both right tabs -> right pane collapses automatically
            Call(viewer, "ClearActiveSideTab");
            Check(!resultsSplit.Panel2Collapsed && Field<LogSession>(viewer, "_current").ActiveSideTab == SidePaneTab.SecondarySearch,
                "clearing pinned switches to remaining secondary tab");
            Call(viewer, "ClearActiveSideTab");
            Check(resultsSplit.Panel2Collapsed, "clearing all right tabs auto-collapses right pane");
            query.Text = "error";
            Pump((Task)Call(viewer, "SearchAsync")!);
            Pump(viewer.OpenFileAsync(first, LogTextEncoding.Gbk));
            Check(Field<LogSession>(viewer, "_current").Document.EncodingKind == LogTextEncoding.Gbk, "host encoding changes cached document");
            Pump(viewer.OpenFileAsync(first, LogTextEncoding.Utf8));
            Check(Field<LogSession>(viewer, "_current").Document.EncodingKind == LogTextEncoding.Utf8 && query.Text == "error", "encoding switch preserves query");

            var sshSample = CreateFile(directory, "sample.log", "INFO one\nERROR remote\n中文 tail\n");
            CreateFile(directory, "sample-extra.log", "INFO two\n");
            using var remoteWorker = PumpResult(StartSshWorkerAsync());
            var remoteDoc = new SshLogDocument(new SshTarget(SshConfig.DefaultPath, "linux-prod", sshSample), remoteWorker);
            Pump(remoteDoc.OpenAsync(LogTextEncoding.Utf8, default));
            _ = viewer.Handle;
            Pump(viewer.OpenRemoteDocumentAsync(remoteDoc));
            Application.DoEvents();
            Check(parent.SshTabPayload == $"{remoteDoc.FilePath}\tsample.log (linux-prod)", "viewer notifies NPP host to create SSH tab");
            Check(Field<LogSession>(viewer, "_current").GetSourceLineText(2) == "ERROR remote", "viewer prefetches remote source page");
            query.Text = "error";
            Pump((Task)Call(viewer, "SearchAsync")!);
            Check(Field<LogSession>(viewer, "_current").MatchCount == 1 && Field<LogSession>(viewer, "_current").GetResultLineText(0) == "ERROR remote", "UI remote search");
            Call(viewer, "DocumentStatus");
            Check(Field<ToolStripStatusLabel>(viewer, "_status").Text!.Contains("linux-prod"), "status bar shows remote host and memory");

            using (var sshDialog = new SshOpenForm(parent.Handle, dark: true) { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false })
            {
                sshDialog.Show();
                using var browser = PumpResult(StartSshWorkerAsync());
                typeof(SshOpenForm).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(sshDialog, browser);
                Field<ComboBox>(sshDialog, "_hosts").Text = "linux-prod";
                typeof(SshOpenForm).GetField("_connectedHost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(sshDialog, "linux-prod");
                Pump((Task)Call(sshDialog, "BrowseAsync", directory, false)!);
                var files = Field<ListBox>(sshDialog, "_files");
                var pathInput = Field<TextBox>(sshDialog, "_path");
                Check(files.Items.Count >= 2, "SSH browser populated");
                pathInput.Text = Path.Combine(directory, "missing");
                pathInput.Text = Path.Combine(directory, "sam");
                var wait = Stopwatch.StartNew();
                while (Field<object?>(sshDialog, "_browseCts") is not null && wait.Elapsed < TimeSpan.FromSeconds(8)) { Application.DoEvents(); Thread.Sleep(15); }
                Check(files.Items.Count == 2 && pathInput.Text == Path.Combine(directory, "sam") && files.SelectedIndex == -1, "live prefix filtering keeps typed text without auto-selecting");
                files.SelectedIndex = 1;
                Application.DoEvents();
                Check(pathInput.Text == sshSample, "selecting remote file fills path");
                sshDialog.Close();
                Check(!browser.IsConnected, "closing SSH dialog disposes unused worker");
            }
            Call(viewer, "CloseSession", 2);
            Check(!remoteWorker.IsConnected, "closing SSH session releases remote worker");
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

    private static Task<SshSession> StartSshWorkerAsync()
    {
        var start = new ProcessStartInfo("python");
        foreach (var argument in new[] { "-u", "-c", SshSession.Bootstrap }) start.ArgumentList.Add(argument);
        return SshSession.StartAsync(start, default);
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
    private static T PumpResult<T>(Task<T> task)
    {
        Pump(task);
        return task.GetAwaiter().GetResult();
    }
    private sealed class ShortcutReceiver : Form
    {
        internal bool ReturnRequested;
        internal int HostCommand;
        internal string? SshTabPayload;
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == Native.HideMessage) ReturnRequested = true;
            if (message.Msg == Native.CommandMessage) HostCommand = message.WParam.ToInt32();
            if (message.Msg == Native.CopyDataMessage)
            {
                var data = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.CopyDataStruct>(message.LParam);
                if (data.dwData == (IntPtr)1 && data.lpData != IntPtr.Zero && data.cbData > 0)
                    SshTabPayload = System.Runtime.InteropServices.Marshal.PtrToStringUni(data.lpData, data.cbData / sizeof(char));
            }
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
