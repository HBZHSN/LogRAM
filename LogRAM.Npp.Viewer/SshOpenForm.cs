using System.Text.Json;

namespace LogRAM.Npp;

internal sealed class SshOpenForm : Form
{
    private readonly ComboBox _hosts = new() { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };
    private readonly TextBox _path = new() { Text = "~", Dock = DockStyle.Fill };
    private readonly ListBox _files = new() { Dock = DockStyle.Fill, DisplayMember = nameof(SshDirectoryEntry.DisplayName), Font = new Font("Consolas", 10), IntegralHeight = false };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 4) };
    private readonly Button _browse = new() { AutoSize = true, Text = Loc.S.SshBrowse, Margin = new Padding(8, 0, 0, 0) };
    private readonly Button _up = new() { Width = 34, Text = "↑", Enabled = false, Margin = new Padding(6, 0, 0, 0) };
    private readonly Button _open = new() { AutoSize = true, MinimumSize = new Size(96, 28), Text = Loc.S.SshOpen };
    private readonly CancellationTokenSource _closed = new();
    private readonly ToolTip _tips = new();
    private SshSession? _session;
    private string? _connectedHost;
    private string? _parent;
    private bool _busy;
    private bool _updatingPath;
    private CancellationTokenSource? _browseCts;

    internal SshLogDocument? Document { get; private set; }

    internal SshOpenForm(IntPtr hostParent, bool dark)
    {
        Text = Loc.S.SshOpen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Font;
        MinimumSize = new Size(520, 380);
        Size = new Size(720, 520);
        MinimizeBox = false;
        MaximizeBox = true;
        ShowIcon = false;
        ShowInTaskbar = false;
        var rootWindow = Native.GetAncestor(hostParent, 2);
        if (rootWindow != IntPtr.Zero && Native.GetWindowRect(rootWindow, out var rect) && rect.Right > rect.Left)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(rect.Left + (rect.Right - rect.Left - Width) / 2, rect.Top + (rect.Bottom - rect.Top - Height) / 2);
        }
        else StartPosition = FormStartPosition.CenterParent;

        _tips.SetToolTip(_up, Loc.S.SshParent);
        var cancel = new Button { AutoSize = true, MinimumSize = new Size(76, 28), Text = Loc.S.CancelButton, DialogResult = DialogResult.Cancel, Margin = new Padding(8, 0, 0, 0) };
        CancelButton = cancel;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(14) };
        for (var i = 0; i < 7; i++)
            layout.RowStyles.Add(new RowStyle(i == 3 ? SizeType.Percent : SizeType.AutoSize, i == 3 ? 100f : 0f));

        var hostRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        hostRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        hostRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        hostRow.Controls.Add(new Label { Text = Loc.S.SshHost, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 10, 0) }, 0, 0);
        hostRow.Controls.Add(_hosts, 1, 0);

        var configLabel = new Label { Text = SshConfig.DefaultPath, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 6) };

        var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 0, 0, 8) };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _browse.Anchor = _up.Anchor = _path.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        pathRow.Controls.Add(_path, 0, 0);
        pathRow.Controls.Add(_browse, 1, 0);
        pathRow.Controls.Add(_up, 2, 0);

        var hintLabel = new Label { Text = Loc.S.SshHint, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 10) };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_open);

        layout.Controls.Add(hostRow, 0, 0);
        layout.Controls.Add(configLabel, 0, 1);
        layout.Controls.Add(pathRow, 0, 2);
        layout.Controls.Add(_files, 0, 3);
        layout.Controls.Add(_status, 0, 4);
        layout.Controls.Add(hintLabel, 0, 5);
        layout.Controls.Add(buttons, 0, 6);
        Controls.Add(layout);

        if (dark)
        {
            var back = Color.FromArgb(30, 30, 30);
            var surface = Color.FromArgb(45, 45, 45);
            var fore = Color.Gainsboro;
            BackColor = surface;
            ForeColor = fore;
            _hosts.BackColor = _path.BackColor = _files.BackColor = back;
            _hosts.ForeColor = _path.ForeColor = _files.ForeColor = fore;
            foreach (var button in new[] { _browse, _up, _open, cancel })
            {
                button.FlatStyle = FlatStyle.Flat;
                button.BackColor = Color.FromArgb(60, 60, 60);
                button.ForeColor = fore;
            }
        }

        try
        {
            var hosts = SshConfig.ReadHosts(SshConfig.DefaultPath);
            _hosts.Items.AddRange(hosts.Cast<object>().ToArray());
            _hosts.Text = hosts.FirstOrDefault() ?? "";
        }
        catch (Exception ex) { _status.Text = ex.Message; }

        _browse.Click += async (_, _) => await BrowseAsync(_path.Text);
        _up.Click += async (_, _) => { if (_parent is not null) await BrowseAsync(_parent); };
        _files.SelectedIndexChanged += (_, _) =>
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
        _files.DoubleClick += async (_, _) =>
        {
            if (_files.SelectedItem is not SshDirectoryEntry entry || _busy) return;
            if (entry.IsDirectory) await BrowseAsync(entry.Path);
            else await OpenAsync();
        };
        _path.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await BrowseAsync(_path.Text);
        };
        _open.Click += async (_, _) => await OpenAsync();
        FormClosed += (_, _) =>
        {
            _closed.Cancel();
            _browseCts?.Cancel();
            if (Document is null) _session?.Dispose();
            _tips.Dispose();
        };
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _hosts.Enabled = _path.Enabled = _files.Enabled = _browse.Enabled = _open.Enabled = !busy;
        _up.Enabled = !busy && _parent is not null;
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
            _files.BeginUpdate();
            try
            {
                _files.Items.Clear();
                foreach (var entry in directory.Entries) _files.Items.Add(entry);
            }
            finally { _files.EndUpdate(); }
            _status.Text = directory.Limited ? Loc.S.SshDirectoryLimited : Loc.S.Ready;
            _up.Enabled = true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested && _hosts.Text.Trim() == host)
            {
                _files.Items.Clear();
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
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { if (!IsDisposed) SetBusy(false); }
    }
}
