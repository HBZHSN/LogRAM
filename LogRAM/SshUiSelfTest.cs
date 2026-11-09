using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
}
