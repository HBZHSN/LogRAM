using System.Diagnostics;

namespace LogRAM.Npp;

internal sealed class ViewerForm : Form
{
    private readonly IntPtr _parent;
    private readonly int _owner;
    private readonly string _settingsPath;
    private readonly ViewerSettings _settings;
    private readonly List<LogSession> _sessions = new();
    private LogSession? _current;
    private CancellationTokenSource? _operation;
    private bool _busy, _dark, _updating, _closing, _dialogOpen;
    private int _openRequestGeneration;
    private Keys _workspaceShortcut = Keys.Control | Keys.Alt | Keys.L;
    private readonly ToolStrip _queryBar = new() { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
    private readonly ToolStripButton _refresh = new(), _live = new() { CheckOnClick = true },
        _search = new(), _cancel = new(), _regex = new() { CheckOnClick = true }, _case = new() { CheckOnClick = true },
        _export = new(), _previous = new(), _next = new(), _go = new();
    private readonly ToolStripComboBox _query = new() { AutoSize = false, Width = 200 };
    private readonly ToolStripLabel _lineLabel = new();
    private readonly ToolStripTextBox _line = new() { Text = "1", AutoSize = false, Width = 80 };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
    private readonly DataGridView _source = CreateGrid(), _results = CreateGrid();
    private readonly VScrollBar _sourceScroll = new() { Dock = DockStyle.Right }, _resultsScroll = new() { Dock = DockStyle.Right };
    private readonly StatusStrip _statusBar = new();
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft }, _count = new();
    private readonly ToolStripProgressBar _progress = new() { Width = 95 };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };

    private string T(string chinese, string english) => _settings.Language == "en" ? english : chinese;
    internal ViewerForm(IntPtr parent, int owner, string configDirectory)
    {
        _parent = parent;
        _owner = owner;
        _settingsPath = Path.Combine(configDirectory, "LogRAM.Npp.json");
        _settings = ViewerSettings.Load(_settingsPath);
        TopLevel = false;
        Text = "LogRAM";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(320, 180);
        Size = new Size(900, 650);
        _query.Items.AddRange(_settings.SearchHistory.Cast<object>().ToArray());
        _queryBar.Items.AddRange(new ToolStripItem[] { _query, _search, _cancel, new ToolStripSeparator(),
            _regex, _case, new ToolStripSeparator(), _refresh, _live, new ToolStripSeparator(),
            _lineLabel, _line, _go, new ToolStripSeparator(), _previous, _next, _export });
        _statusBar.Items.AddRange(new ToolStripItem[] { _status, _count, _progress });
        _split.Panel1.Controls.Add(_source);
        _split.Panel1.Controls.Add(_sourceScroll);
        _split.Panel2.Controls.Add(_results);
        _split.Panel2.Controls.Add(_resultsScroll);
        _split.Panel2.Controls.Add(_queryBar);
        Controls.Add(_split);
        Controls.Add(_statusBar);
        _refresh.Click += async (_, _) => await ReloadAsync();
        _search.Click += async (_, _) => await SearchAsync();
        _cancel.Click += (_, _) => _operation?.Cancel();
        _export.Click += async (_, _) => await ExportAsync();
        _go.Click += (_, _) => GoToLine();
        _previous.Click += (_, _) => NavigateMatch(-1);
        _next.Click += (_, _) => NavigateMatch(1);
        _live.CheckedChanged += (_, _) =>
        {
            if (!_updating && _current is not null) _current.Live = _live.Checked;
            _live.ToolTipText = _live.Checked ? T("停止实时刷新", "Stop live refresh") : T("实时增量刷新当前文件", "Incrementally refresh the current file");
        };
        _query.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await SearchAsync(); } };
        _line.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; GoToLine(); } };
        _source.CellValueNeeded += (_, e) => FillCell(e, false);
        _results.CellValueNeeded += (_, e) => FillCell(e, true);
        _source.CellFormatting += (_, e) =>
        {
            if (_current is not null && _current.MatchCount > 0 && _selectedMatch >= 0 && _selectedMatch < _current.MatchCount &&
                _current.SourceTop + e.RowIndex == _current.MatchLine(_selectedMatch))
                e.CellStyle!.BackColor = _dark ? Color.FromArgb(75, 65, 20) : Color.FromArgb(255, 245, 185);
        };
        _results.CellClick += (_, e) => { if (e.RowIndex >= 0) ShowMatch((_current?.ResultsTop ?? 0) + e.RowIndex); };
        _sourceScroll.Scroll += (_, _) => { if (!_updating && _current is not null) { _current.SourceTop = _sourceScroll.Value + 1L; UpdatePages(); } };
        _resultsScroll.Scroll += (_, _) => { if (!_updating && _current is not null) { _current.ResultsTop = _resultsScroll.Value; UpdatePages(); } };
        _source.MouseWheel += (_, e) => ScrollPage(false, -Math.Sign(e.Delta) * 3);
        _results.MouseWheel += (_, e) => ScrollPage(true, -Math.Sign(e.Delta) * 3);
        _split.Panel1.Resize += (_, _) => UpdatePages();
        _split.Panel2.Resize += (_, _) => UpdatePages();
        _queryBar.SizeChanged += (_, _) => ResizeQuery();
        _timer.Tick += async (_, _) => await TickAsync();
        Translate();
        UpdateToolbarIcons();
        SetBusy(false);
    }

    private static DataGridView CreateGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, VirtualMode = true, ReadOnly = true, AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, AllowUserToResizeRows = false, AutoGenerateColumns = false,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true, ScrollBars = ScrollBars.Horizontal, BorderStyle = BorderStyle.None,
            ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText,
            Font = new Font("Consolas", 10), ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
        };
        grid.RowTemplate.Height = 22;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Line", HeaderText = "Line", Width = 90, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Text", HeaderText = "Text", Width = 1600, SortMode = DataGridViewColumnSortMode.NotSortable });
        return grid;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // WinForms assigns the owner during Show; attach only after that step finishes.
        Native.SetWindowLong(Handle, -16, (Native.GetWindowLong(Handle, -16) & ~unchecked((int)0x80000000)) | 0x40000000);
        Native.SetParent(Handle, _parent);
        if (Native.GetParent(Handle) != _parent) throw new System.ComponentModel.Win32Exception();
        if (Native.GetClientRect(_parent, out var rect)) Bounds = new Rectangle(0, 0, rect.Right, rect.Bottom);
        _split.SplitterDistance = Math.Max(50, _split.Height / 2);
        Native.PostMessage(_parent, Native.ReadyMessage, Handle, IntPtr.Zero);
        _timer.Start();
        _ = Task.Run(ReadCommandsAsync);
    }

    private async Task ReadCommandsAsync()
    {
        try
        {
            while (await Console.In.ReadLineAsync() is { } command)
            {
                if (_closing) break;
                BeginInvoke(new Action(async () =>
                {
                    if (command.StartsWith("open\t", StringComparison.Ordinal))
                    {
                        var generation = _openRequestGeneration;
                        while ((_busy || _dialogOpen) && !_closing && generation == _openRequestGeneration) await Task.Delay(20);
                        if (_closing || generation != _openRequestGeneration) return;
                        var parts = command[5..].Split('\t', 2);
                        if (parts.Length == 2 && parts[0] is "utf8" or "gbk")
                            await OpenFileAsync(parts[1], parts[0] == "gbk" ? LogTextEncoding.Gbk : LogTextEncoding.Utf8);
                    }
                    else if (command == "focus") { _query.Focus(); UpdatePages(); }
                    else if (command.StartsWith("theme\t", StringComparison.Ordinal)) ApplyTheme(command.EndsWith('1'));
                    else if (command.StartsWith("language\t", StringComparison.Ordinal))
                    {
                        _settings.Language = command[9..] == "zh" ? "zh" : "en";
                        Translate();
                    }
                    else if (command.StartsWith("shortcut\t", StringComparison.Ordinal) && int.TryParse(command[9..], out var shortcut))
                        _workspaceShortcut = (Keys)shortcut;
                    else if (command.StartsWith("close\t", StringComparison.Ordinal))
                    {
                        var path = command[6..];
                        if (_current?.Document.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase) == true) _operation?.Cancel();
                        while (_busy && !_closing) await Task.Delay(20);
                        if (_closing) return;
                        var index = _sessions.FindIndex(item => item.Document.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));
                        if (index >= 0) CloseSession(index);
                    }
                    else if (command == "closeall")
                    {
                        _openRequestGeneration++;
                        _operation?.Cancel();
                        while (_busy && !_closing) await Task.Delay(20);
                        if (_closing) return;
                        foreach (var session in _sessions) session.Dispose();
                        _sessions.Clear(); _current = null; SelectSession(-1);
                        GC.Collect();
                    }
                }));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        if (!_closing && IsHandleCreated) BeginInvoke(new Action(Close));
    }

    internal async Task OpenFileAsync(string path, LogTextEncoding? requestedEncoding = null)
    {
        if (_busy || _closing) return;
        await RunOperationAsync(async token =>
        {
            path = Path.GetFullPath(path);
            var existing = _sessions.FindIndex(item => item.Document.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) { SelectSession(existing); return; }
            _status.Text = T("正在加载日志…", "Loading log…");
            var encoding = requestedEncoding ?? LogTextEncoding.Utf8;
            var progress = new Progress<(long BytesRead, long TotalBytes)>(value => UpdateProgress(value.BytesRead, value.TotalBytes));
            var document = await Task.Run(() => LogFileDocument.Open(path, encoding, progress, token), token);
            if (_closing) { document.Dispose(); return; }
            _sessions.Add(new LogSession(document));
            SelectSession(_sessions.Count - 1);
            DocumentStatus();
        });
        if (!_closing && requestedEncoding is { } selectedEncoding && _current?.Document.EncodingKind != selectedEncoding &&
            _current?.Document.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase) == true)
            await ReloadAsync(selectedEncoding);
    }

    private void SelectSession(int index)
    {
        if (_current is not null)
        {
            _current.QueryText = _query.Text;
            _current.Regex = _regex.Checked;
            _current.CaseSensitive = _case.Checked;
        }
        _current = index >= 0 && index < _sessions.Count ? _sessions[index] : null;
        _selectedMatch = -1;
        _updating = true;
        _query.Text = _current?.QueryText ?? "";
        _regex.Checked = _current?.Regex ?? false;
        _case.Checked = _current?.CaseSensitive ?? false;
        _live.Checked = _current?.Live ?? false;
        _updating = false;
        UpdatePages();
        SetBusy(_busy);
        DocumentStatus();
    }

    private void CloseSession(int index)
    {
        var session = _sessions[index];
        _sessions.RemoveAt(index);
        session.Dispose();
        if (_current == session) _current = null;
        SelectSession(_current is null ? Math.Min(index, _sessions.Count - 1) : _sessions.IndexOf(_current));
    }

    private async Task ReloadAsync(LogTextEncoding? requestedEncoding = null)
    {
        if (_busy || _current is null) return;
        var session = _current;
        session.QueryText = _query.Text;
        session.Regex = _regex.Checked;
        session.CaseSensitive = _case.Checked;
        var encoding = requestedEncoding ?? session.Document.EncodingKind;
        await RunOperationAsync(async token =>
        {
            _status.Text = T("正在重新加载…", "Reloading…");
            var loadProgress = new Progress<(long BytesRead, long TotalBytes)>(value => UpdateProgress(value.BytesRead, value.TotalBytes));
            var document = await Task.Run(() => LogFileDocument.Open(session.Document.FilePath, encoding, loadProgress, token), token);
            if (_closing) { document.Dispose(); return; }
            var replacement = new LogSession(document) { SourceTop = session.SourceTop, QueryText = session.QueryText, Regex = session.Regex,
                CaseSensitive = session.CaseSensitive, Live = session.Live };
            var index = _sessions.IndexOf(session);
            _sessions[index] = replacement;
            session.Dispose();
            _current = null;
            SelectSession(index);
        });
    }

    private async Task SearchAsync()
    {
        if (_busy || _current is null) return;
        if (string.IsNullOrWhiteSpace(_query.Text)) { _query.Focus(); return; }
        var session = _current;
        await RunOperationAsync(async token =>
        {
            _status.Text = T("正在搜索…", "Searching…");
            _selectedMatch = -1;
            var watch = Stopwatch.StartNew();
            var count = await session.SearchAsync(_query.Text, _regex.Checked, _case.Checked, token,
                new Progress<LogSearchProgress>(value => UpdateProgress(value.BytesRead, value.TotalBytes)));
            if (_closing) return;
            ViewerSettings.Remember(_settings.SearchHistory, session.QueryText);
            _query.Items.Clear();
            _query.Items.AddRange(_settings.SearchHistory.Cast<object>().ToArray());
            _query.Text = session.QueryText;
            _settings.Save(_settingsPath);
            _status.Text = T($"匹配 {count:N0} 行，用时 {watch.Elapsed.TotalSeconds:F2} 秒", $"{count:N0} matches in {watch.Elapsed.TotalSeconds:F2}s");
            UpdatePages();
            if (count > 0) ShowMatch(0);
        });
    }

    private async Task ExportAsync()
    {
        if (_busy || _dialogOpen || _current is null || _current.MatchCount == 0) return;
        using var dialog = new SaveFileDialog { Filter = "UTF-8 text|*.txt", FileName = "LogRAM-results.txt" };
        _dialogOpen = true;
        try { if (dialog.ShowDialog(this) != DialogResult.OK) return; }
        finally { _dialogOpen = false; }
        if (_closing || _current is null) return;
        var path = Path.GetFullPath(dialog.FileName);
        if (_sessions.Any(item => item.Document.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            ShowError(T("不能覆盖已打开的日志文件。", "Cannot overwrite an open source log."));
            return;
        }
        await RunOperationAsync(async token =>
        {
            _status.Text = T("正在导出…", "Exporting…");
            await _current.ExportAsync(path, token);
            _status.Text = T("导出完成", "Export complete");
        });
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> action)
    {
        if (_busy) return;
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        SetBusy(true);
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { if (!_closing) _status.Text = T("已取消，保留已返回的结果", "Cancelled; returned matches retained"); }
        catch (Exception ex) { if (!_closing) ShowError(ex.Message); }
        finally
        {
            _operation = null;
            if (!_closing) { SetBusy(false); UpdatePages(); }
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _refresh.Enabled = _live.Enabled = _search.Enabled = _query.Enabled =
            _regex.Enabled = _case.Enabled = !busy && _current is not null;
        _export.Enabled = !busy && (_current?.MatchCount ?? 0) > 0;
        _cancel.Enabled = busy;
        _progress.Visible = busy;
        _progress.Value = 0;
    }

    private void UpdateProgress(long done, long total)
    {
        if (!_closing && _busy) _progress.Value = total <= 0 ? 0 : (int)Math.Clamp(100.0 * done / total, 0, 100);
    }

    private int VisibleRows(DataGridView grid) => Math.Max(1, (grid.ClientSize.Height - grid.ColumnHeadersHeight - 20) / grid.RowTemplate.Height);
    private void UpdatePages()
    {
        if (_updating || _closing || _current is { IsRefreshing: true } || (IsHandleCreated && !Native.IsWindowVisible(Handle))) return;
        _updating = true;
        var lines = (int)(_current?.Document.LineCount ?? 0);
        var matches = _current?.MatchCount ?? 0;
        var sourceRows = VisibleRows(_source);
        var resultRows = VisibleRows(_results);
        if (_current is not null)
        {
            _current.SourceTop = Math.Clamp(_current.SourceTop, 1, Math.Max(1, lines - sourceRows + 1));
            _current.ResultsTop = Math.Clamp(_current.ResultsTop, 0, Math.Max(0, matches - resultRows));
        }
        SetScroll(_sourceScroll, lines, sourceRows, (int)(_current?.SourceTop ?? 1) - 1);
        SetScroll(_resultsScroll, matches, resultRows, _current?.ResultsTop ?? 0);
        _source.RowCount = Math.Min(sourceRows, lines);
        _results.RowCount = Math.Min(resultRows, matches);
        _source.Invalidate();
        _results.Invalidate();
        ResizeTextColumn(_source, false);
        ResizeTextColumn(_results, true);
        _count.Text = T($"{lines:N0} 行 | {matches:N0} 条结果", $"{lines:N0} lines | {matches:N0} matches");
        _updating = false;
    }

    private void ResizeTextColumn(DataGridView grid, bool results)
    {
        var width = Math.Max(600, grid.ClientSize.Width - 95);
        if (_current is not null)
        {
            for (var row = 0; row < grid.RowCount; row++)
            {
                var index = _current.ResultsTop + row;
                if (results && index >= _current.MatchCount) break;
                var line = results ? _current.MatchLine(index) : _current.SourceTop + row;
                width = Math.Max(width, TextRenderer.MeasureText(_current.Document.GetLineText(line), grid.Font).Width + 16);
            }
        }
        // shortcut: DataGridView columns stop at 65536 px; copy a cell to inspect longer lines.
        grid.Columns[1].Width = Math.Min(65536, width);
    }

    private static void SetScroll(VScrollBar scroll, int total, int page, int value)
    {
        scroll.Maximum = Math.Max(0, total - 1);
        scroll.LargeChange = Math.Min(page, Math.Max(1, total));
        scroll.SmallChange = 1;
        scroll.Value = Math.Clamp(value, 0, Math.Max(0, scroll.Maximum - scroll.LargeChange + 1));
        scroll.Enabled = total > page;
    }

    private void FillCell(DataGridViewCellValueEventArgs e, bool results)
    {
        if (_current is null || _current.IsRefreshing) return;
        var index = _current.ResultsTop + e.RowIndex;
        if (results && index >= _current.MatchCount) return;
        var line = results ? _current.MatchLine(index) : _current.SourceTop + e.RowIndex;
        if (line > _current.Document.LineCount) return;
        e.Value = e.ColumnIndex == 0 ? line : _current.Document.GetLineText(line);
    }

    private void ScrollPage(bool results, int delta)
    {
        if (_current is null) return;
        if (results) _current.ResultsTop = (int)Math.Clamp((long)_current.ResultsTop + delta, 0, Math.Max(0, _current.MatchCount - VisibleRows(_results)));
        else _current.SourceTop = Math.Clamp(_current.SourceTop + delta, 1, Math.Max(1, _current.Document.LineCount - VisibleRows(_source) + 1));
        UpdatePages();
    }

    private int _selectedMatch = -1;
    private void ShowMatch(int index)
    {
        if (_current is null || index < 0 || index >= _current.MatchCount) return;
        _selectedMatch = index;
        var line = _current.MatchLine(index);
        _current.SourceTop = Math.Max(1, line - VisibleRows(_source) / 2);
        if (index < _current.ResultsTop || index >= _current.ResultsTop + VisibleRows(_results)) _current.ResultsTop = index;
        _line.Text = line.ToString();
        UpdatePages();
        _source.ClearSelection();
        var row = (int)(line - _current.SourceTop);
        if (row >= 0 && row < _source.RowCount) _source.Rows[row].Selected = true;
        _results.ClearSelection();
        var resultRow = index - _current.ResultsTop;
        if (resultRow >= 0 && resultRow < _results.RowCount) _results.Rows[resultRow].Selected = true;
    }

    private void NavigateMatch(int direction)
    {
        if (_current is null || _current.MatchCount == 0) return;
        ShowMatch(_selectedMatch < 0 ? (direction > 0 ? 0 : _current.MatchCount - 1) : Math.Clamp(_selectedMatch + direction, 0, _current.MatchCount - 1));
    }

    private void GoToLine()
    {
        if (_current is null) return;
        if (!long.TryParse(_line.Text, out var line) || line < 1 || line > _current.Document.LineCount)
        {
            ShowError(T("请输入日志范围内的行号。", "Enter a line number within this log."));
            return;
        }
        _current.SourceTop = line;
        UpdatePages();
        var row = (int)(line - _current.SourceTop);
        _source.ClearSelection();
        if (row >= 0 && row < _source.RowCount) _source.Rows[row].Selected = true;
        _source.Focus();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_workspaceShortcut != Keys.None && keyData == _workspaceShortcut)
        {
            Native.PostMessage(_parent, Native.HideMessage, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
        switch (keyData)
        {
            case Keys.Control | Keys.F: _query.Focus(); return true;
            case Keys.Control | Keys.G: _line.Focus(); _line.SelectAll(); return true;
            case Keys.Control | Keys.O: Native.PostMessage(_parent, Native.CommandMessage, (IntPtr)41002, IntPtr.Zero); return true;
            case Keys.Control | Keys.W: Native.PostMessage(_parent, Native.CommandMessage, (IntPtr)41003, IntPtr.Zero); return true;
            case Keys.Control | Keys.N: Native.PostMessage(_parent, Native.CommandMessage, (IntPtr)41001, IntPtr.Zero); return true;
            case Keys.Control | Keys.Tab: Native.PostMessage(_parent, Native.CommandMessage, (IntPtr)44095, IntPtr.Zero); return true;
            case Keys.Control | Keys.Shift | Keys.Tab: Native.PostMessage(_parent, Native.CommandMessage, (IntPtr)44096, IntPtr.Zero); return true;
            case Keys.F3: NavigateMatch(1); return true;
            case Keys.Shift | Keys.F3: NavigateMatch(-1); return true;
            case Keys.Escape: _operation?.Cancel(); return true;
            case Keys.PageDown when _source.Focused || _results.Focused: ScrollPage(_results.Focused, VisibleRows(_results.Focused ? _results : _source)); return true;
            case Keys.PageUp when _source.Focused || _results.Focused: ScrollPage(_results.Focused, -VisibleRows(_results.Focused ? _results : _source)); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private async Task TickAsync()
    {
        if (_closing) return;
        if (!Native.IsWindow(_parent)) { Close(); return; }
        try { using var process = Process.GetProcessById(_owner); if (process.HasExited) { Close(); return; } }
        catch (ArgumentException) { Close(); return; }
        if (_busy) { UpdatePages(); return; }
        if (_current is not { Live: true } session) return;
        try { if (!session.HasPendingRefresh && new FileInfo(session.Document.FilePath).Length == session.Document.FileSize) return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            session.Live = false; _live.Checked = false; _status.Text = ex.Message; return;
        }
        await RunOperationAsync(async token =>
        {
            var follow = session.SourceTop + VisibleRows(_source) >= session.Document.LineCount;
            LogFileDocument.AppendResult appended;
            try { appended = await session.RefreshAsync(token); }
            catch
            {
                session.Live = false;
                _live.Checked = false;
                throw;
            }
            if (appended.IsTruncated)
            {
                session.Live = false;
                _live.Checked = false;
                _status.Text = T("日志已截断，请刷新重新加载。", "Log truncated; refresh to reload.");
                return;
            }
            if (!appended.HasNewContent) return;
            if (follow) session.SourceTop = Math.Max(1, session.Document.LineCount - VisibleRows(_source) + 1);
            DocumentStatus();
        });
    }

    private void DocumentStatus() => _status.Text = _current is null ? T("打开日志开始查看", "Open a log to begin") :
        $"{Path.GetFileName(_current.Document.FilePath)} | {_current.Document.EncodingKind} | {_current.Document.MemoryUsage / 1048576.0:N1} MB";

    private void ShowError(string message)
    {
        _status.Text = message;
        MessageBox.Show(this, message, "LogRAM", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void ResizeQuery()
    {
        var controlsWidth = _queryBar.Items.Cast<ToolStripItem>().Where(item => item != _query)
            .Sum(item => item.GetPreferredSize(Size.Empty).Width + item.Margin.Horizontal);
        _query.Width = Math.Clamp(_queryBar.ClientSize.Width - controlsWidth - _query.Margin.Horizontal - 12, 120, 500);
    }

    private void UpdateToolbarIcons()
    {
        foreach (var (button, action) in new[] { (_search, "search"), (_cancel, "cancel"), (_refresh, "refresh"),
            (_live, "live"), (_go, "go"), (_previous, "previous"), (_next, "next"), (_export, "export") })
        {
            var previous = button.Image;
            button.Image = ToolbarIcons.Create(action, _dark, Math.Max(18, 20 * DeviceDpi / 96));
            button.ImageScaling = ToolStripItemImageScaling.None;
            button.DisplayStyle = ToolStripItemDisplayStyle.Image;
            previous?.Dispose();
        }
        ResizeQuery();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        UpdateToolbarIcons();
    }

    private void Translate()
    {
        _refresh.Text = T("刷新", "Refresh"); _live.Text = T("实时", "Live");
        _search.Text = T("搜索", "Search"); _cancel.Text = T("取消", "Cancel"); _regex.Text = T("正则", "Regex");
        _case.Text = T("大小写", "Case"); _export.Text = T("导出", "Export");
        _previous.Text = T("上一结果", "Previous"); _next.Text = T("下一结果", "Next");
        _lineLabel.Text = T("行号", "Line"); _go.Text = T("跳转", "Go");
        _source.Columns[0].HeaderText = _results.Columns[0].HeaderText = T("行号", "Line");
        _source.Columns[1].HeaderText = T("原文 / 上下文", "Source / context");
        _results.Columns[1].HeaderText = T("搜索结果", "Search results");
        foreach (ToolStripItem item in _queryBar.Items) if (item is ToolStripButton) item.AccessibleName = item.ToolTipText = item.Text;
        _regex.Text = ".*"; _case.Text = "Aa";
        _regex.ToolTipText = T("正则表达式", "Regular expression"); _case.ToolTipText = T("区分大小写", "Match case");
        _search.ToolTipText = T("开始搜索（Enter / Ctrl+F）", "Search (Enter / Ctrl+F)");
        _cancel.ToolTipText = T("取消当前操作（Esc）", "Cancel current operation (Esc)");
        _refresh.ToolTipText = T("重新读取当前文件", "Reload current file");
        _live.ToolTipText = _live.Checked ? T("停止实时刷新", "Stop live refresh") : T("实时增量刷新当前文件", "Incrementally refresh the current file");
        _go.ToolTipText = T("跳转到指定行（Ctrl+G）", "Go to line (Ctrl+G)");
        _previous.ToolTipText = T("上一结果（Shift+F3）", "Previous result (Shift+F3)");
        _next.ToolTipText = T("下一结果（F3）", "Next result (F3)");
        _export.ToolTipText = T("导出搜索结果", "Export search results");
        _query.ToolTipText = T("关键词、正则或 in(...);notin(...)，Enter 搜索", "Text, regex or in(...);notin(...); Enter to search");
        _query.AccessibleName = _query.ComboBox.AccessibleName = T("搜索条件", "Search query");
        _line.AccessibleName = _line.TextBox.AccessibleName = T("跳转行号", "Go to line number");
        _source.AccessibleName = T("日志原文", "Source log");
        _results.AccessibleName = T("搜索结果列表", "Search result list");
        ResizeQuery();
        DocumentStatus();
    }

    private void ApplyTheme(bool dark)
    {
        _dark = dark;
        var back = dark ? Color.FromArgb(30, 30, 30) : SystemColors.Window;
        var fore = dark ? Color.Gainsboro : SystemColors.WindowText;
        BackColor = back;
        foreach (var grid in new[] { _source, _results })
        {
            grid.BackgroundColor = back;
            grid.BackColor = back;
            grid.DefaultCellStyle.BackColor = back;
            grid.DefaultCellStyle.ForeColor = fore;
            grid.DefaultCellStyle.SelectionBackColor = dark ? Color.FromArgb(55, 75, 95) : SystemColors.Highlight;
            grid.DefaultCellStyle.SelectionForeColor = dark ? Color.White : SystemColors.HighlightText;
            grid.EnableHeadersVisualStyles = false;
            var headerBack = dark ? Color.FromArgb(45, 45, 45) : SystemColors.Control;
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = headerBack, ForeColor = fore, SelectionBackColor = headerBack, SelectionForeColor = fore
            };
            grid.GridColor = dark ? Color.FromArgb(55, 55, 55) : SystemColors.ControlLight;
        }
        foreach (var bar in new ToolStrip[] { _queryBar, _statusBar })
        {
            bar.BackColor = dark ? Color.FromArgb(45, 45, 45) : SystemColors.Control;
            bar.ForeColor = fore;
            bar.Renderer = new ToolStripProfessionalRenderer(new ViewerColors(dark));
        }
        _query.BackColor = back; _query.ForeColor = fore;
        _line.BackColor = back; _line.ForeColor = fore;
        _split.BackColor = dark ? Color.FromArgb(55, 55, 55) : SystemColors.Control;
        _split.Panel1.BackColor = _split.Panel2.BackColor = back;
        UpdateToolbarIcons();
        _source.Refresh(); _results.Refresh();
        Invalidate(true);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        _timer.Stop();
        _operation?.Cancel();
        _settings.Save(_settingsPath);
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            foreach (ToolStripItem item in _queryBar.Items) item.Image?.Dispose();
            // shortcut: an active operation owns its document until process exit; do not dispose while its search threads run.
            if (!_busy) { foreach (var session in _sessions) session.Dispose(); _sessions.Clear(); }
        }
        base.Dispose(disposing);
    }

    private sealed class ViewerColors(bool dark) : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => dark ? Color.FromArgb(45, 45, 45) : base.ToolStripGradientBegin;
        public override Color ToolStripGradientMiddle => ToolStripGradientBegin;
        public override Color ToolStripGradientEnd => ToolStripGradientBegin;
        public override Color MenuItemSelected => dark ? Color.FromArgb(65, 65, 65) : base.MenuItemSelected;
        public override Color ButtonCheckedGradientBegin => dark ? Color.FromArgb(65, 85, 105) : base.ButtonCheckedGradientBegin;
        public override Color ButtonCheckedGradientMiddle => ButtonCheckedGradientBegin;
        public override Color ButtonCheckedGradientEnd => ButtonCheckedGradientBegin;
        public override Color ButtonSelectedGradientBegin => dark ? Color.FromArgb(65, 85, 105) : base.ButtonSelectedGradientBegin;
        public override Color ButtonSelectedGradientMiddle => ButtonSelectedGradientBegin;
        public override Color ButtonSelectedGradientEnd => ButtonSelectedGradientBegin;
        public override Color ButtonPressedGradientBegin => ButtonSelectedGradientBegin;
        public override Color ButtonPressedGradientMiddle => ButtonSelectedGradientBegin;
        public override Color ButtonPressedGradientEnd => ButtonSelectedGradientBegin;
        public override Color ToolStripDropDownBackground => ToolStripGradientBegin;
    }
}
