using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LogRAM;

internal sealed class SshOpenDialog : Window
{
    private readonly ComboBox _hosts = new() { IsEditable = true, MinWidth = 180 };
    private readonly TextBox _path = new() { Text = "~", Margin = new Thickness(0, 8, 8, 8) };
    private readonly ListBox _files = new() { DisplayMemberPath = nameof(SshDirectoryEntry.DisplayName), FontFamily = new FontFamily("Consolas") };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly Button _browse = new() { Content = Loc.S.SshBrowse, Margin = new Thickness(0, 8, 0, 8) };
    private readonly Button _up = new() { Content = "↑", ToolTip = Loc.S.SshParent, Margin = new Thickness(8, 8, 0, 8), IsEnabled = false };
    private readonly Button _open = new() { Content = Loc.S.SshOpen, MinWidth = 90, IsDefault = true };
    private readonly CancellationTokenSource _closed = new();
    private SshSession? _session;
    private string? _connectedHost;
    private string? _parent;
    private bool _busy;
    private bool _updatingPath;
    private CancellationTokenSource? _browseCts;

    internal SshLogDocument? Document { get; private set; }

    internal SshOpenDialog(Window owner)
    {
        Owner = owner;
        Title = Loc.S.SshOpen;
        Width = 720;
        Height = 520;
        MinWidth = 520;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _files.SetResourceReference(Control.BackgroundProperty, "LogBackBrush");
        _files.SetResourceReference(Control.ForegroundProperty, "TextBrush");

        var grid = new Grid { Margin = new Thickness(14) };
        for (var i = 0; i < 7; i++) grid.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var hostRow = new DockPanel();
        hostRow.Children.Add(new TextBlock { Text = Loc.S.SshHost, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        hostRow.Children.Add(_hosts);
        Add(grid, hostRow, 0);
        Add(grid, new TextBlock { Text = SshConfig.DefaultPath, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap }, 1);
        var pathRow = new DockPanel();
        DockPanel.SetDock(_up, Dock.Right);
        DockPanel.SetDock(_browse, Dock.Right);
        pathRow.Children.Add(_up);
        pathRow.Children.Add(_browse);
        pathRow.Children.Add(_path);
        Add(grid, pathRow, 2);
        Add(grid, _files, 3);
        Add(grid, _status, 4);
        Add(grid, new TextBlock { Text = Loc.S.SshHint, TextWrapping = TextWrapping.Wrap }, 5);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = Loc.S.CancelButton, IsCancel = true, Margin = new Thickness(8, 0, 0, 0), MinWidth = 70 };
        buttons.Children.Add(_open);
        buttons.Children.Add(cancel);
        Add(grid, buttons, 6);
        Content = grid;

        try
        {
            var hosts = SshConfig.ReadHosts(SshConfig.DefaultPath);
            _hosts.ItemsSource = hosts;
            _hosts.Text = hosts.FirstOrDefault() ?? "";
        }
        catch (Exception ex) { _status.Text = ex.Message; }

        _browse.Click += async (_, _) => await BrowseAsync(_path.Text);
        _up.Click += async (_, _) => { if (_parent is not null) await BrowseAsync(_parent); };
        _files.SelectionChanged += (_, _) =>
        {
            if (_files.SelectedItem is SshDirectoryEntry { IsDirectory: false } entry)
            {
                _browseCts?.Cancel();
                SetPath(entry.Path);
            }
        };
        _path.TextChanged += async (_, _) =>
        {
            if (!_updatingPath && !_busy && _session is { IsConnected: true })
                await BrowseAsync(_path.Text, filter: true);
        };
        _files.MouseDoubleClick += async (_, _) =>
        {
            if (_files.SelectedItem is not SshDirectoryEntry entry || _busy) return;
            if (entry.IsDirectory) await BrowseAsync(entry.Path);
            else await OpenAsync();
        };
        _path.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await BrowseAsync(_path.Text);
        };
        _open.Click += async (_, _) => await OpenAsync();
        Closed += (_, _) =>
        {
            _closed.Cancel();
            _browseCts?.Cancel();
            if (Document is null) _session?.Dispose();
        };
    }

    private static void Add(Grid grid, UIElement element, int row)
    {
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _hosts.IsEnabled = _path.IsEnabled = _files.IsEnabled = _browse.IsEnabled = _open.IsEnabled = !busy;
        _up.IsEnabled = !busy && _parent is not null;
        if (busy) _status.Text = Loc.S.Loading;
    }

    private void SetPath(string path)
    {
        _updatingPath = true;
        _path.Text = path;
        _updatingPath = false;
    }

    private async Task EnsureSessionAsync(CancellationToken token)
    {
        var host = _hosts.Text.Trim();
        if (_session is { IsConnected: true } && host == _connectedHost) return;
        _session?.Dispose();
        _session = null;
        _session = await SshSession.ConnectAsync(SshConfig.DefaultPath, host, token);
        _connectedHost = host;
    }

    private async Task BrowseAsync(string path, bool filter = false)
    {
        if (_busy) return;
        _browseCts?.Cancel();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        _browseCts = cts;
        var host = _hosts.Text.Trim();
        try
        {
            if (filter) await Task.Delay(250, cts.Token);
            _status.Text = Loc.S.Loading;
            await EnsureSessionAsync(cts.Token);
            var data = await _session!.RequestAsync("list", new { path, filter }, cts.Token);
            if (cts.IsCancellationRequested || _hosts.Text.Trim() != host) return;
            var directory = data.Deserialize<SshDirectory>(SshSession.JsonOptions)!;
            if (!filter && directory.Prefix.Length == 0) SetPath(directory.Path.TrimEnd('/', '\\') + "/");
            _parent = directory.Parent;
            _files.ItemsSource = directory.Entries;
            _status.Text = directory.Limited ? Loc.S.SshDirectoryLimited : Loc.S.Ready;
            _up.IsEnabled = true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested && _hosts.Text.Trim() == host)
            {
                _files.ItemsSource = null;
                _status.Text = ex.Message;
            }
        }
        finally { if (ReferenceEquals(_browseCts, cts)) _browseCts = null; }
    }

    private async Task OpenAsync()
    {
        if (_busy) return;
        _browseCts?.Cancel();
        SetBusy(true);
        try
        {
            await EnsureSessionAsync(_closed.Token);
            var target = new SshTarget(SshConfig.DefaultPath, _connectedHost!, _path.Text);
            var document = new SshLogDocument(target, _session!);
            await document.OpenAsync(LogTextEncoding.Utf8, _closed.Token);
            _closed.Token.ThrowIfCancellationRequested();
            Document = document;
            DialogResult = true;
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { SetBusy(false); }
    }
}
