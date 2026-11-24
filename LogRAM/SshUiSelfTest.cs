using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LogRAM;

internal static class SshUiSelfTest
{
    internal static async Task<int> RunAsync(string outputPath)
    {
        Directory.CreateDirectory(outputPath);
        var path = Path.Combine(outputPath, "sample.log");
        await File.WriteAllTextAsync(path, "INFO first\nERROR first\nINFO middle\n中文错误\nERROR last\n");
        await File.WriteAllTextAsync(Path.Combine(outputPath, "sample-other.log"), "INFO other\n");
        MainWindow? window = null;
        SshOpenDialog? dialog = null;
        try
        {
            window = new MainWindow { Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false, Width = 1100, Height = 700 };
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(Get<TextBlock>(window, "MemoryStatusTextBlock").ToolTip?.ToString() == Loc.S.LocalMemoryTip, "local process memory is shown before opening a file");
            var memoryLabel = Get<TextBlock>(window, "MemoryStatusTextBlock");
            var root = (FrameworkElement)window.Content;
            Check(Math.Abs(memoryLabel.TranslatePoint(new Point(memoryLabel.ActualWidth / 2, 0), root).X - root.ActualWidth / 2) <= 1, "local memory display is centered in the window");
            using var session = await StartWorkerAsync();
            var remote = new SshLogDocument(new SshTarget(SshConfig.DefaultPath, "test", path), session);
            await remote.OpenAsync(LogTextEncoding.Utf8, default);
            await (Task)Call(window, "OpenOrSelectTabAsync", remote.FilePath, remote)!;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(Get<TextBox>(window, "LogContentBox").Text.Contains("INFO first"), "remote page rendered");
            await (Task)Call(window, "RefreshRemoteMemoryStatusAsync")!;
            Call(window, "UpdateMemoryStatus");
            Check(remote.MemoryStatus is { ProcessBytes: > 0, AvailableBytes: >= 0 }, "remote memory values are measured");
            var expectedMemory = Loc.S.MemoryActive((string)Call(window, "FormatBytes", remote.MemoryStatus!.AvailableBytes)!, (string)Call(window, "FormatBytes", remote.MemoryUsage)!);
            Check(Get<TextBlock>(window, "MemoryStatusTextBlock").Text == expectedMemory, "footer uses the selected remote host and worker rather than local memory");
            await remote.SearchAsync("error", false, false, batch => Call(window, "AddSearchResultBatch", batch), null, default);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(Get<TextBox>(window, "SearchContentBox").Text.Contains("ERROR last"), "remote search results rendered");
            Check(Get<TextBlock>(window, "SearchResultStatusTextBlock").ToolTip?.ToString()?.Contains("1 MiB") == true, "remote result limit is visible");
            var firstPage = (Task)Call(window, "LoadPageByLineNumberAsync", 1L, true)!;
            var lastPage = (Task)Call(window, "LoadPageByLineNumberAsync", 4L, true)!;
            await Task.WhenAll(firstPage, lastPage);
            Check(Get<TextBox>(window, "LogContentBox").Text.StartsWith("中文错误"), "rapid paging discards stale replies");
            Snapshot(window, Path.Combine(outputPath, "ssh-client.png"));

            window.Width = 820;
            window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(Get<Button>(window, "SshButton").IsEnabled, "SSH control remains enabled at minimum width");
            Check(Math.Abs(memoryLabel.TranslatePoint(new Point(memoryLabel.ActualWidth / 2, 0), root).X - root.ActualWidth / 2) <= 1, "remote memory uses the same centered display at minimum width");
            Snapshot(window, Path.Combine(outputPath, "ssh-client-narrow.png"));

            // Verify Requirement 1 (TextBox leading whitespace fix), Requirement 2 (case-insensitive advanced search),
            // Requirement 3 (line-number pinning to right pane), and Requirement 4 (Ctrl+F in-results find + All + tab switching).
            window.Width = 1100;
            window.UpdateLayout();
            var localLogPath = Path.Combine(outputPath, "advanced-pin-find.log");
            await File.WriteAllTextAsync(localLogPath,
                "INFO 100 service started\n" +
                "ERROR 404 resource missing\n" +
                "WARN 200 ok retry\n" +
                "error 404 gateway timeout\n" +
                "INFO 500 downstream failed\n");
            await (Task)Call(window, "OpenOrSelectTabAsync", localLogPath, null!)!;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);

            // Req 1: check TextBox character 0 X offset inside AdvancedSearchPopup & SearchTextBox (no doubled padding)
            var themeEnum = typeof(MainWindow).GetNestedType("AppTheme", BindingFlags.NonPublic)!;
            var originalTheme = Get<object>(window, "_currentTheme");
            Call(window, "ApplyTheme", Enum.Parse(themeEnum, "Dark"));
            var searchBox = Get<TextBox>(window, "SearchTextBox");
            searchBox.Text = "in(error,500);notin(200)";
            window.UpdateLayout();
            var searchChar0 = searchBox.GetRectFromCharacterIndex(0);
            Check(!searchChar0.IsEmpty && searchChar0.Left <= 8.5, $"SearchTextBox leading inset too large: {searchChar0.Left:F1}");

            var advPopup = Get<Popup>(window, "AdvancedSearchPopup");
            advPopup.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var includePanel = Get<StackPanel>(window, "AdvancedIncludePanel");
            var firstAdvBox = includePanel.Children.OfType<Grid>().First().Children.OfType<TextBox>().First();
            firstAdvBox.UpdateLayout();
            var advChar0 = firstAdvBox.GetRectFromCharacterIndex(0);
            Check(!advChar0.IsEmpty && advChar0.Left <= 8.5, $"Advanced TextBox leading inset too large: {advChar0.Left:F1}");
            if (advPopup.Child is FrameworkElement popupChild)
            {
                SnapshotElement(popupChild, Path.Combine(outputPath, "advanced-popup-dark.png"));
            }
            advPopup.IsOpen = false;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);

            // Req 2: case-insensitive advanced search with mixed letter + pure numeric keywords
            Get<ToggleButton>(window, "CaseSensitiveButton").IsChecked = false;
            Get<ToggleButton>(window, "RegexButton").IsChecked = false;
            await (Task)Call(window, "StartSearchAsync")!;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var searchResults = Get<RangeObservableCollection<LogSearchResult>>(window, "_searchResults");
            Check(searchResults.Count == 3 && searchResults[0].LineNumber == 2 && searchResults[1].LineNumber == 4 && searchResults[2].LineNumber == 5,
                $"Case-insensitive advanced search expected 3 matches (2,4,5), got {searchResults.Count}");

            // Req 3: pin lines out of order -> sorted ascending in right pane
            window.TogglePinResult(searchResults[2], switchToPinnedTab: true); // line 5
            window.TogglePinResult(searchResults[0], switchToPinnedTab: true); // line 2
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(Get<Border>(window, "SearchRightPane").Visibility == Visibility.Visible, "right pane opens when pinning");
            Check(Get<TextBox>(window, "SideLineNumberBox").Text == "2\n5", "pinned lines sorted ascending chronologically");

            // Req 4: Ctrl+F secondary search, ↑/↓ navigation, All button, and dual-tab switching
            window.OpenResultFindBar();
            Get<TextBox>(window, "ResultFindTextBox").Text = "404";
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(Get<Border>(window, "ResultFindBar").Visibility == Visibility.Visible, "floating result find bar opens");
            Check(Get<TextBlock>(window, "ResultFindCountTextBlock").Text == "1/2", "result find counts matches");
            window.NavigateResultFindMatch(1);
            Check(Get<TextBlock>(window, "ResultFindCountTextBlock").Text == "2/2", "result find next match");
            window.NavigateResultFindMatch(1);
            Check(Get<TextBlock>(window, "ResultFindCountTextBlock").Text == "1/2", "result find wraps around");
            window.CommitSecondarySearchAll();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var pinnedTab = Get<ToggleButton>(window, "PinnedTabButton");
            var secondaryTab = Get<ToggleButton>(window, "SecondarySearchTabButton");
            Check(pinnedTab.Visibility == Visibility.Visible && secondaryTab.Visibility == Visibility.Visible &&
                secondaryTab.IsChecked == true, "both pinned and secondary tabs visible with secondary active");
            Check(Get<TextBox>(window, "SideLineNumberBox").Text == "2\n4", "secondary tab displays all matched lines");
            Snapshot(window, Path.Combine(outputPath, "search-pane-dark.png"));

            // Narrow width check with both panes & floating find bar visible
            window.Width = 820;
            window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Snapshot(window, Path.Combine(outputPath, "search-pane-narrow.png"));

            // Light theme visual check (without writing user settings)
            window.Width = 1100;
            Call(window, "ApplyTheme", Enum.Parse(themeEnum, "Light"));
            window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Snapshot(window, Path.Combine(outputPath, "search-pane-light.png"));
            Call(window, "ApplyTheme", originalTheme);
            window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);

            // Switch back to Pinned tab and verify pins persist after a brand new primary search
            Call(window, "PinnedTabButton_Click", pinnedTab, new RoutedEventArgs());
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(Get<TextBox>(window, "SideLineNumberBox").Text == "2\n5", "switching back to pinned tab shows pinned lines");
            searchBox.Text = "in(404)";
            await (Task)Call(window, "StartSearchAsync")!;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(searchResults.Count == 2 && Get<TextBox>(window, "SideLineNumberBox").Text == "2\n5" &&
                secondaryTab.Visibility == Visibility.Collapsed, "new pure-numeric case-insensitive search keeps pinned lines and clears secondary tab");
            window.CloseResultFindBar();
            Check(Get<Border>(window, "ResultFindBar").Visibility == Visibility.Collapsed, "closing find bar hides it");
            Call(window, "ClearRightPaneButton_Click", Get<Button>(window, "ClearRightPaneButton"), new RoutedEventArgs());
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(Get<Border>(window, "SearchRightPane").Visibility == Visibility.Collapsed, "clearing all pinned lines collapses right pane");

            dialog = new SshOpenDialog(window) { Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
            dialog.Show();
            var hosts = Get<ComboBox>(dialog, "_hosts");
            hosts.ApplyTemplate();
            Check(hosts.Template.FindName("PART_EditableTextBox", hosts) is TextBox { IsVisible: true }, "SSH host selection supports visible text input");
            hosts.Text = "test-host";
            Check(((TextBox)hosts.Template.FindName("PART_EditableTextBox", hosts)).Text == "test-host", "typed SSH host is displayed");
            using var browser = await StartWorkerAsync();
            Set(dialog, "_session", browser);
            Set(dialog, "_connectedHost", Get<ComboBox>(dialog, "_hosts").Text);
            await (Task)Call(dialog, "BrowseAsync", outputPath, false)!;
            Check(Get<ListBox>(dialog, "_files").Items.Count >= 1, "remote directory browser populated");
            var pathInput = Get<TextBox>(dialog, "_path");
            pathInput.Text = Path.Combine(outputPath, "missing");
            pathInput.Text = Path.Combine(outputPath, "sam");
            Check(pathInput.IsEnabled, "path remains editable during live filtering");
            await WaitUntilAsync(() => Get<object?>(dialog, "_browseCts") is null);
            var files = Get<ListBox>(dialog, "_files");
            Check(files.Items.Count == 2 && files.Items[0] is SshDirectoryEntry { Name: "sample-other.log" }
                && files.Items[1] is SshDirectoryEntry { Name: "sample.log" }, "typing a path prefix automatically refreshes matching files");
            Check(pathInput.Text == Path.Combine(outputPath, "sam"), "autocomplete keeps the typed text and ignores stale replies");
            files.SelectedIndex = 1;
            await Task.Delay(350);
            Check(files.Items.Count == 2 && pathInput.Text == path, "selecting a file preserves the list for double-click/open");
            Snapshot(dialog, Path.Combine(outputPath, "ssh-browser.png"));
            dialog.Close();
            dialog = null;
            window.Close();
            window = null;
            Check(!session.IsConnected && !browser.IsConnected, "closing windows releases workers");
            File.WriteAllText(Path.Combine(outputPath, "result.txt"), "PASS: live path-prefix filtering/cancellation, file selection, local/remote measured memory, remote tabs, rendered paging/search, limits, minimum-width toolbar and worker cleanup.");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(outputPath, "result.txt"), ex.ToString());
            return 1;
        }
        finally
        {
            dialog?.Close();
            window?.Close();
        }
    }

    private static Task<SshSession> StartWorkerAsync()
    {
        var start = new ProcessStartInfo("python");
        foreach (var argument in new[] { "-u", "-c", SshSession.Bootstrap }) start.ArgumentList.Add(argument);
        return SshSession.StartAsync(start, default);
    }

    private static T Get<T>(object instance, string field) => (T)instance.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Set(object instance, string field, object value) => instance.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static object? Call(object instance, string method, params object[] arguments) => instance.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(instance, arguments);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("UI refresh did not finish.");
            await Task.Delay(20);
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }

    private static void Snapshot(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void SnapshotElement(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
