namespace LogRAM.Npp;

internal sealed partial class ViewerForm
{
    private readonly ToolStripButton _advanced = new() { DisplayStyle = ToolStripItemDisplayStyle.Text };
    private readonly SplitContainer _resultsSplit = new()
    {
        Dock = DockStyle.Fill,
        Orientation = Orientation.Vertical,
        Panel1MinSize = 60,
        Panel2MinSize = 60,
        Panel2Collapsed = true
    };
    private readonly Panel _sideHeader = new() { Dock = DockStyle.Top, Height = 28, Padding = new Padding(4, 2, 4, 2) };
    private readonly Button _pinnedTabBtn = new() { Height = 23, AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 4, 0) };
    private readonly Button _secondaryTabBtn = new() { Height = 23, AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(0) };
    private readonly Button _sideClearBtn = new() { Height = 23, Width = 50, Dock = DockStyle.Right, FlatStyle = FlatStyle.Flat };
    private readonly FlowLayoutPanel _sideTabsFlow = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = false,
        Margin = new Padding(0),
        Padding = new Padding(0)
    };
    private readonly DataGridView _sideGrid = CreateGrid();
    private readonly VScrollBar _sideScroll = new() { Dock = DockStyle.Right };

    private readonly Panel _resultFindBar = new()
    {
        BorderStyle = BorderStyle.FixedSingle,
        Height = 32,
        Width = 342,
        Visible = false,
        Anchor = AnchorStyles.Top | AnchorStyles.Right
    };
    private readonly TextBox _resultFindInput = new() { BorderStyle = BorderStyle.FixedSingle, Width = 138, Location = new Point(5, 4) };
    private readonly CheckBox _resultFindCase = new()
    {
        Appearance = Appearance.Button,
        FlatStyle = FlatStyle.Flat,
        Text = "Aa",
        TextAlign = ContentAlignment.MiddleCenter,
        Size = new Size(30, 23),
        Location = new Point(147, 4)
    };
    private readonly Label _resultFindCount = new()
    {
        AutoSize = false,
        TextAlign = ContentAlignment.MiddleCenter,
        Size = new Size(54, 23),
        Location = new Point(180, 4)
    };
    private readonly Button _resultFindPrev = new() { FlatStyle = FlatStyle.Flat, Text = "↑", Size = new Size(24, 23), Location = new Point(236, 4) };
    private readonly Button _resultFindNext = new() { FlatStyle = FlatStyle.Flat, Text = "↓", Size = new Size(24, 23), Location = new Point(262, 4) };
    private readonly Button _resultFindAll = new() { FlatStyle = FlatStyle.Flat, Size = new Size(44, 23), Location = new Point(289, 4) };
    private readonly Button _resultFindClose = new() { FlatStyle = FlatStyle.Flat, Text = "×", Size = new Size(24, 23), Location = new Point(336, 4) };
    private readonly ToolTip _searchPaneTip = new();
    private readonly System.Windows.Forms.Timer _resultFindDebounce = new() { Interval = 120 };
    private readonly ContextMenuStrip _resultsMenu = new();
    private readonly ToolStripMenuItem _resultsPinMenuItem = new();
    private readonly ToolStripMenuItem _resultsCopyMenuItem = new();
    private readonly ContextMenuStrip _sideMenu = new();
    private readonly ToolStripMenuItem _sidePinMenuItem = new();
    private readonly ToolStripMenuItem _sideCopyMenuItem = new();

    private long _highlightedSourceLine = -1;
    private double _resultsSplitRatio = 0.58;
    private bool _syncingResultFind;
    private bool _resultFindDirty;
    private int _resultsContextRow = -1;
    private int _sideContextRow = -1;

    private void InitializeSearchPaneControls()
    {
        _resultFindBar.Width = 366;
        _resultFindBar.Controls.AddRange(new Control[]
        {
            _resultFindInput, _resultFindCase, _resultFindCount,
            _resultFindPrev, _resultFindNext, _resultFindAll, _resultFindClose
        });

        _sideTabsFlow.Controls.Add(_pinnedTabBtn);
        _sideTabsFlow.Controls.Add(_secondaryTabBtn);
        _sideHeader.Controls.Add(_sideTabsFlow);
        _sideHeader.Controls.Add(_sideClearBtn);

        _resultsSplit.Panel1.Controls.Add(_results);
        _resultsSplit.Panel1.Controls.Add(_resultsScroll);
        _resultsSplit.Panel1.Controls.Add(_resultFindBar);
        _resultFindBar.BringToFront();

        _resultsSplit.Panel2.Controls.Add(_sideGrid);
        _resultsSplit.Panel2.Controls.Add(_sideScroll);
        _resultsSplit.Panel2.Controls.Add(_sideHeader);

        _resultsMenu.Items.Add(_resultsPinMenuItem);
        _resultsMenu.Items.Add(new ToolStripSeparator());
        _resultsMenu.Items.Add(_resultsCopyMenuItem);
        _results.ContextMenuStrip = _resultsMenu;

        _sideMenu.Items.Add(_sidePinMenuItem);
        _sideMenu.Items.Add(new ToolStripSeparator());
        _sideMenu.Items.Add(_sideCopyMenuItem);
        _sideGrid.ContextMenuStrip = _sideMenu;

        _advanced.Click += async (_, _) => await OpenAdvancedSearchDialogAsync();

        _results.CellMouseEnter += (_, e) => _results.Cursor = e.RowIndex >= 0 && e.ColumnIndex == 0 ? Cursors.Hand : Cursors.Default;
        _results.CellMouseLeave += (_, _) => _results.Cursor = Cursors.Default;
        _results.CellFormatting += Results_CellFormatting;
        _results.CellPainting += Results_CellPainting;
        _results.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0) _resultsContextRow = e.RowIndex;
        };
        _resultsMenu.Opening += ResultsMenu_Opening;
        _resultsPinMenuItem.Click += ResultsPinMenuItem_Click;
        _resultsCopyMenuItem.Click += (_, _) => CopySelectedGridCells(_results);

        _sideGrid.CellValueNeeded += SideGrid_CellValueNeeded;
        _sideGrid.CellClick += SideGrid_CellClick;
        _sideGrid.CellMouseEnter += (_, e) => _sideGrid.Cursor = e.RowIndex >= 0 && e.ColumnIndex == 0 ? Cursors.Hand : Cursors.Default;
        _sideGrid.CellMouseLeave += (_, _) => _sideGrid.Cursor = Cursors.Default;
        _sideGrid.CellFormatting += SideGrid_CellFormatting;
        _sideGrid.CellPainting += SideGrid_CellPainting;
        _sideGrid.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0) _sideContextRow = e.RowIndex;
        };
        _sideGrid.MouseWheel += (_, e) => ScrollSidePage(-Math.Sign(e.Delta) * 3);
        _sideScroll.Scroll += (_, _) =>
        {
            if (!_updating && _current is not null)
            {
                SetActiveSideTop(_sideScroll.Value);
                UpdateSidePage();
            }
        };
        _sideMenu.Opening += SideMenu_Opening;
        _sidePinMenuItem.Click += SidePinMenuItem_Click;
        _sideCopyMenuItem.Click += (_, _) => CopySelectedGridCells(_sideGrid);

        _pinnedTabBtn.Click += (_, _) => SelectSideTab(SidePaneTab.Pinned);
        _secondaryTabBtn.Click += (_, _) => SelectSideTab(SidePaneTab.SecondarySearch);
        _sideClearBtn.Click += (_, _) => ClearActiveSideTab();

        _resultsSplit.Panel1.Resize += (_, _) => { PositionResultFindBar(); UpdatePages(); };
        _resultsSplit.Panel2.Resize += (_, _) => UpdateSidePage();
        _resultsSplit.SplitterMoved += (_, _) =>
        {
            if (!_resultsSplit.Panel2Collapsed && _resultsSplit.Width > 0)
                _resultsSplitRatio = Math.Clamp((double)_resultsSplit.SplitterDistance / _resultsSplit.Width, 0.25, 0.80);
        };

        _resultFindDebounce.Tick += (_, _) =>
        {
            _resultFindDebounce.Stop();
            if (_resultFindDirty) ExecuteResultFind(navigateToSource: false);
        };
        _resultFindInput.TextChanged += (_, _) =>
        {
            if (_syncingResultFind || _current is null) return;
            if (_current.MatchCount > 5000)
            {
                _resultFindDirty = true;
                _resultFindDebounce.Stop();
                _resultFindDebounce.Start();
            }
            else
            {
                _resultFindDebounce.Stop();
                _resultFindDirty = false;
                ExecuteResultFind(navigateToSource: false);
            }
        };
        _resultFindInput.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                NavigateResultFind(e.Shift ? -1 : 1);
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                CloseResultFindBar();
            }
        };
        _resultFindCase.CheckedChanged += (_, _) =>
        {
            if (_syncingResultFind || _current is null) return;
            _resultFindDebounce.Stop();
            _resultFindDirty = false;
            ExecuteResultFind(navigateToSource: false);
        };
        _resultFindPrev.Click += (_, _) => NavigateResultFind(-1);
        _resultFindNext.Click += (_, _) => NavigateResultFind(1);
        _resultFindAll.Click += (_, _) => CommitSecondarySearchAll();
        _resultFindClose.Click += (_, _) => CloseResultFindBar();
    }

    private void DisposeSearchPaneResources()
    {
        _resultFindDebounce.Stop();
        _resultFindDebounce.Dispose();
        _searchPaneTip.Dispose();
        _resultsMenu.Dispose();
        _sideMenu.Dispose();
    }

    private async Task OpenAdvancedSearchDialogAsync()
    {
        if (_busy || _dialogOpen || _closing || _current is null) return;
        using var dialog = new AdvancedSearchForm(_query.Text, _dark);
        _dialogOpen = true;
        SetBusy(_busy);
        DialogResult result;
        try { result = dialog.ShowDialog(this); }
        finally { _dialogOpen = false; if (!_closing) SetBusy(_busy); }
        if (_closing || result != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.QueryText)) return;
        _regex.Checked = false;
        _query.Text = dialog.QueryText;
        await SearchAsync();
    }

    private void HandleResultsCellClick(DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || _current is null) return;
        var matchIndex = _current.ResultsTop + e.RowIndex;
        if (matchIndex < 0 || matchIndex >= _current.MatchCount) return;

        if (e.ColumnIndex == 0)
        {
            var pinned = _current.TogglePin(_current.MatchLine(matchIndex), _current.GetResultLineText(matchIndex));
            if (pinned) _current.ActiveSideTab = SidePaneTab.Pinned;
            UpdateSidePane();
            _results.Invalidate();
            return;
        }

        ShowMatch(matchIndex);
    }

    private void JumpSourceToLine(long line)
    {
        if (_current is null || line < 1 || line > _current.Document.LineCount) return;
        _highlightedSourceLine = line;
        _current.SourceTop = Math.Max(1, line - VisibleRows(_source) / 2);
        _line.Text = line.ToString();
        UpdatePages();
        _source.ClearSelection();
        var row = (int)(line - _current.SourceTop);
        if (row >= 0 && row < _source.RowCount) _source.Rows[row].Selected = true;
    }

    private void PositionResultFindBar()
    {
        var rightEdge = _resultsSplit.Panel1.ClientSize.Width - _resultsScroll.Width - 6;
        var x = Math.Max(4, rightEdge - _resultFindBar.Width);
        var y = Math.Max(4, _results.ColumnHeadersHeight + 4);
        _resultFindBar.Location = new Point(x, y);
    }

    private bool IsFocusInResultsArea()
    {
        if (_results.Focused || _sideGrid.Focused || _resultFindInput.Focused ||
            _resultsSplit.ContainsFocus || _resultFindBar.ContainsFocus)
            return true;
        var control = ActiveControl;
        while (control is not null)
        {
            if (control == _resultsSplit || control == _results || control == _sideGrid || control == _resultFindBar)
                return true;
            control = control.Parent;
        }
        return false;
    }

    internal void OpenResultFindBar()
    {
        if (_current is null) return;
        _current.IsResultFindOpen = true;
        PositionResultFindBar();
        _resultFindBar.Visible = true;
        _resultFindBar.BringToFront();
        _syncingResultFind = true;
        _resultFindInput.Text = _current.ResultFindQuery;
        _resultFindCase.Checked = _current.ResultFindCaseSensitive;
        _syncingResultFind = false;
        _resultFindInput.Focus();
        _resultFindInput.SelectAll();
        if (!string.IsNullOrEmpty(_resultFindInput.Text))
            ExecuteResultFind(navigateToSource: false);
        else
            UpdateResultFindUi();
    }

    internal void CloseResultFindBar()
    {
        _resultFindDebounce.Stop();
        _resultFindDirty = false;
        if (_current is not null) _current.IsResultFindOpen = false;
        _resultFindBar.Visible = false;
        _results.Invalidate();
        _results.Focus();
    }

    private void FlushPendingResultFind()
    {
        if (_resultFindDirty)
        {
            _resultFindDebounce.Stop();
            ExecuteResultFind(navigateToSource: false);
        }
    }

    private void ExecuteResultFind(bool navigateToSource, int preferredResultIndex = -1)
    {
        _resultFindDirty = false;
        _resultFindDebounce.Stop();
        if (_current is null)
        {
            UpdateResultFindUi();
            return;
        }

        var count = _current.UpdateResultFindMatches(_resultFindInput.Text, _resultFindCase.Checked, preferredResultIndex);
        if (count > 0) RevealCurrentResultFindMatch(navigateToSource);
        else _results.Invalidate();
        UpdateResultFindUi();
    }

    internal void NavigateResultFind(int delta)
    {
        FlushPendingResultFind();
        if (_current is null || _current.ResultFindMatchCount == 0) return;
        var count = _current.ResultFindMatchCount;
        var next = _current.CurrentResultFindIndex < 0
            ? (delta >= 0 ? 0 : count - 1)
            : (_current.CurrentResultFindIndex + delta + count) % count;
        _current.CurrentResultFindIndex = next;
        RevealCurrentResultFindMatch(navigateToSource: true);
        UpdateResultFindUi();
    }

    private void RevealCurrentResultFindMatch(bool navigateToSource)
    {
        if (_current is null || _current.CurrentResultFindIndex < 0 || _current.CurrentResultFindIndex >= _current.ResultFindMatchCount)
            return;

        var resultIndex = _current.ResultFindMatchAt(_current.CurrentResultFindIndex);
        if (resultIndex < 0 || resultIndex >= _current.MatchCount) return;

        if (navigateToSource)
        {
            ShowMatch(resultIndex);
            return;
        }

        _selectedMatch = resultIndex;
        var rows = VisibleRows(_results);
        if (resultIndex < _current.ResultsTop || resultIndex >= _current.ResultsTop + rows)
        {
            _current.ResultsTop = Math.Clamp(resultIndex - rows / 2, 0, Math.Max(0, _current.MatchCount - rows));
            UpdatePages();
        }
        else
        {
            _results.Invalidate();
        }
        _results.ClearSelection();
        var row = resultIndex - _current.ResultsTop;
        if (row >= 0 && row < _results.RowCount) _results.Rows[row].Selected = true;
    }

    internal void CommitSecondarySearchAll()
    {
        FlushPendingResultFind();
        if (_current is null || _current.ResultFindMatchCount == 0) return;
        _current.CommitSecondarySearchAll();
        UpdateSidePane();
    }

    private void SyncResultFindBarFromSession()
    {
        _resultFindDebounce.Stop();
        _resultFindDirty = false;
        _syncingResultFind = true;
        _resultFindInput.Text = _current?.ResultFindQuery ?? "";
        _resultFindCase.Checked = _current?.ResultFindCaseSensitive ?? false;
        _syncingResultFind = false;
        var open = _current?.IsResultFindOpen == true;
        if (open) PositionResultFindBar();
        _resultFindBar.Visible = open;
        UpdateResultFindUi();
    }

    private void UpdateResultFindUi()
    {
        var query = _resultFindInput.Text;
        var count = _current?.ResultFindMatchCount ?? 0;
        if (string.IsNullOrEmpty(query))
        {
            _resultFindCount.Text = "";
        }
        else
        {
            var current = count > 0 && _current is not null && _current.CurrentResultFindIndex >= 0
                ? _current.CurrentResultFindIndex + 1
                : 0;
            _resultFindCount.Text = Loc.S.ResultFindMatchCount(current, count);
        }

        var hasMatches = count > 0;
        _resultFindPrev.Enabled = hasMatches;
        _resultFindNext.Enabled = hasMatches;
        _resultFindAll.Enabled = hasMatches;
    }

    internal void SelectSideTab(SidePaneTab tab)
    {
        if (_current is null) return;
        if (tab == SidePaneTab.Pinned && _current.PinnedCount == 0) return;
        if (tab == SidePaneTab.SecondarySearch && _current.SecondaryCount == 0) return;
        _current.ActiveSideTab = tab;
        UpdateSidePane();
    }

    private void ClearActiveSideTab()
    {
        if (_current is null) return;
        if (_current.ActiveSideTab == SidePaneTab.Pinned)
        {
            _current.ClearPinned();
            _results.Invalidate();
        }
        else
        {
            _current.ClearSecondary();
        }
        UpdateSidePane();
    }

    private void UpdateSidePane()
    {
        var pinnedCount = _current?.PinnedCount ?? 0;
        var secondaryCount = _current?.SecondaryCount ?? 0;
        var hasAny = _current is not null && (pinnedCount > 0 || secondaryCount > 0);

        if (!hasAny)
        {
            _resultsSplit.Panel2Collapsed = true;
            _sideGrid.RowCount = 0;
            PositionResultFindBar();
            return;
        }

        var session = _current!;
        if (session.ActiveSideTab == SidePaneTab.Pinned && pinnedCount == 0)
            session.ActiveSideTab = SidePaneTab.SecondarySearch;
        else if (session.ActiveSideTab == SidePaneTab.SecondarySearch && secondaryCount == 0)
            session.ActiveSideTab = SidePaneTab.Pinned;

        var wasCollapsed = _resultsSplit.Panel2Collapsed;
        _resultsSplit.Panel2Collapsed = false;
        if (wasCollapsed)
        {
            var min = _resultsSplit.Panel1MinSize;
            var max = _resultsSplit.Width - _resultsSplit.Panel2MinSize - _resultsSplit.SplitterWidth;
            if (max > min)
                _resultsSplit.SplitterDistance = Math.Clamp((int)Math.Round(_resultsSplit.Width * _resultsSplitRatio), min, max);
        }

        _pinnedTabBtn.Visible = pinnedCount > 0;
        _pinnedTabBtn.Text = Loc.S.PinnedTab(pinnedCount);
        _secondaryTabBtn.Visible = secondaryCount > 0;
        _secondaryTabBtn.Text = Loc.S.SecondarySearchTab(secondaryCount);
        _searchPaneTip.SetToolTip(_secondaryTabBtn, string.IsNullOrEmpty(session.SecondaryQuery) ? "" : session.SecondaryQuery);
        _searchPaneTip.SetToolTip(_sideClearBtn, session.ActiveSideTab == SidePaneTab.Pinned ? Loc.S.ClearPinnedTip : Loc.S.ClearSecondaryTip);

        _sideGrid.Columns[1].HeaderText = session.ActiveSideTab == SidePaneTab.Pinned
            ? Loc.S.PinnedTab(pinnedCount)
            : Loc.S.SecondarySearchTab(secondaryCount);

        StyleSideTabButtons();
        UpdateSidePage();
        PositionResultFindBar();
    }

    private int GetActiveSideCount()
    {
        if (_current is null) return 0;
        return _current.ActiveSideTab == SidePaneTab.Pinned ? _current.PinnedCount : _current.SecondaryCount;
    }

    private int GetActiveSideTop()
    {
        if (_current is null) return 0;
        return _current.ActiveSideTab == SidePaneTab.Pinned ? _current.PinnedTop : _current.SecondaryTop;
    }

    private void SetActiveSideTop(int value)
    {
        if (_current is null) return;
        if (_current.ActiveSideTab == SidePaneTab.Pinned) _current.PinnedTop = value;
        else _current.SecondaryTop = value;
    }

    private PinnedResultLine GetActiveSideItem(int index)
    {
        return _current!.ActiveSideTab == SidePaneTab.Pinned
            ? _current.PinnedAt(index)
            : _current.SecondaryAt(index);
    }

    private void UpdateSidePage()
    {
        if (_closing || _resultsSplit.Panel2Collapsed) return;
        var count = GetActiveSideCount();
        var rows = VisibleRows(_sideGrid);
        var top = Math.Clamp(GetActiveSideTop(), 0, Math.Max(0, count - rows));
        SetActiveSideTop(top);
        SetScroll(_sideScroll, count, rows, top);
        _sideGrid.RowCount = Math.Min(rows, count);
        _sideGrid.Invalidate();
        ResizeSideTextColumn();
    }

    private void ResizeSideTextColumn()
    {
        var width = Math.Max(300, _sideGrid.ClientSize.Width - 95);
        var count = GetActiveSideCount();
        var top = GetActiveSideTop();
        for (var row = 0; row < _sideGrid.RowCount; row++)
        {
            var idx = top + row;
            if (idx >= count) break;
            width = Math.Max(width, TextRenderer.MeasureText(GetActiveSideItem(idx).Text, _sideGrid.Font).Width + 16);
        }
        _sideGrid.Columns[1].Width = Math.Min(65536, width);
    }

    private void ScrollSidePage(int delta)
    {
        if (_current is null || _resultsSplit.Panel2Collapsed) return;
        var count = GetActiveSideCount();
        var target = (int)Math.Clamp((long)GetActiveSideTop() + delta, 0, Math.Max(0, count - VisibleRows(_sideGrid)));
        SetActiveSideTop(target);
        UpdateSidePage();
    }

    private void SideGrid_CellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (_current is null) return;
        var idx = GetActiveSideTop() + e.RowIndex;
        if (idx < 0 || idx >= GetActiveSideCount()) return;
        var item = GetActiveSideItem(idx);
        e.Value = e.ColumnIndex == 0 ? item.LineNumber : item.Text;
    }

    private void SideGrid_CellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || _current is null) return;
        var idx = GetActiveSideTop() + e.RowIndex;
        if (idx < 0 || idx >= GetActiveSideCount()) return;
        var item = GetActiveSideItem(idx);
        if (e.ColumnIndex == 0)
        {
            _current.TogglePin(item.LineNumber, item.Text);
            UpdateSidePane();
            _results.Invalidate();
            return;
        }

        JumpSourceToLine(item.LineNumber);
    }

    private void Results_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (_current is null || e.RowIndex < 0) return;
        var matchIndex = _current.ResultsTop + e.RowIndex;
        if (matchIndex < 0 || matchIndex >= _current.MatchCount) return;

        if (_current.IsResultFindOpen && _current.CurrentResultFindIndex >= 0 &&
            _current.CurrentResultFindIndex < _current.ResultFindMatchCount &&
            _current.ResultFindMatchAt(_current.CurrentResultFindIndex) == matchIndex)
        {
            e.CellStyle!.BackColor = _dark ? Color.FromArgb(45, 78, 102) : Color.FromArgb(207, 232, 247);
        }

        if (e.ColumnIndex == 0 && _current.IsPinned(_current.MatchLine(matchIndex)))
        {
            e.CellStyle!.ForeColor = _dark ? Color.FromArgb(75, 163, 211) : Color.FromArgb(27, 111, 157);
        }
    }

    private void Results_CellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.ColumnIndex != 0 || e.RowIndex < 0 || _current is null) return;
        var matchIndex = _current.ResultsTop + e.RowIndex;
        if (matchIndex < 0 || matchIndex >= _current.MatchCount) return;
        if (!_current.IsPinned(_current.MatchLine(matchIndex))) return;

        PaintPinAccentBar(e);
    }

    private void SideGrid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (_current is null || e.RowIndex < 0 || e.ColumnIndex != 0) return;
        var idx = GetActiveSideTop() + e.RowIndex;
        if (idx < 0 || idx >= GetActiveSideCount()) return;
        if (_current.IsPinned(GetActiveSideItem(idx).LineNumber))
        {
            e.CellStyle!.ForeColor = _dark ? Color.FromArgb(75, 163, 211) : Color.FromArgb(27, 111, 157);
        }
    }

    private void SideGrid_CellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.ColumnIndex != 0 || e.RowIndex < 0 || _current is null) return;
        var idx = GetActiveSideTop() + e.RowIndex;
        if (idx < 0 || idx >= GetActiveSideCount()) return;
        if (!_current.IsPinned(GetActiveSideItem(idx).LineNumber)) return;

        PaintPinAccentBar(e);
    }

    private void PaintPinAccentBar(DataGridViewCellPaintingEventArgs e)
    {
        e.Paint(e.CellBounds, DataGridViewPaintParts.All);
        var accent = _dark ? Color.FromArgb(75, 163, 211) : Color.FromArgb(27, 111, 157);
        using var brush = new SolidBrush(accent);
        e.Graphics!.FillRectangle(brush, e.CellBounds.Left, e.CellBounds.Top, 3, e.CellBounds.Height);
        e.Handled = true;
    }

    private void ResultsMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var row = _resultsContextRow >= 0 ? _resultsContextRow : (_results.SelectedRows.Count > 0 ? _results.SelectedRows[0].Index : -1);
        var matchIndex = _current is not null && row >= 0 ? _current.ResultsTop + row : -1;
        var valid = _current is not null && matchIndex >= 0 && matchIndex < _current.MatchCount;
        _resultsPinMenuItem.Enabled = valid;
        _resultsPinMenuItem.Tag = valid ? matchIndex : null;
        _resultsPinMenuItem.Text = valid && _current!.IsPinned(_current.MatchLine(matchIndex))
            ? Loc.S.UnpinLineMenu
            : Loc.S.PinLineMenu;
        _resultsCopyMenuItem.Text = Loc.S.MenuCopy;
        _resultsCopyMenuItem.Enabled = _results.SelectedCells.Count > 0;
    }

    private void ResultsPinMenuItem_Click(object? sender, EventArgs e)
    {
        if (_current is null || _resultsPinMenuItem.Tag is not int matchIndex || matchIndex < 0 || matchIndex >= _current.MatchCount) return;
        var pinned = _current.TogglePin(_current.MatchLine(matchIndex), _current.GetResultLineText(matchIndex));
        if (pinned) _current.ActiveSideTab = SidePaneTab.Pinned;
        UpdateSidePane();
        _results.Invalidate();
    }

    private void SideMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var row = _sideContextRow >= 0 ? _sideContextRow : (_sideGrid.SelectedRows.Count > 0 ? _sideGrid.SelectedRows[0].Index : -1);
        var idx = _current is not null && row >= 0 ? GetActiveSideTop() + row : -1;
        var valid = _current is not null && idx >= 0 && idx < GetActiveSideCount();
        _sidePinMenuItem.Enabled = valid;
        _sidePinMenuItem.Tag = valid ? idx : null;
        _sidePinMenuItem.Text = valid && _current!.IsPinned(GetActiveSideItem(idx).LineNumber)
            ? Loc.S.UnpinLineMenu
            : Loc.S.PinLineMenu;
        _sideCopyMenuItem.Text = Loc.S.MenuCopy;
        _sideCopyMenuItem.Enabled = _sideGrid.SelectedCells.Count > 0;
    }

    private void SidePinMenuItem_Click(object? sender, EventArgs e)
    {
        if (_current is null || _sidePinMenuItem.Tag is not int idx || idx < 0 || idx >= GetActiveSideCount()) return;
        var item = GetActiveSideItem(idx);
        _current.TogglePin(item.LineNumber, item.Text);
        UpdateSidePane();
        _results.Invalidate();
    }

    private static void CopySelectedGridCells(DataGridView grid)
    {
        var data = grid.GetClipboardContent();
        if (data is not null) Clipboard.SetDataObject(data);
    }

    private void TranslateSearchPane()
    {
        var s = Loc.S;
        _advanced.Text = s.AdvancedButtonShort;
        _advanced.AccessibleName = s.AdvancedButton;
        _advanced.ToolTipText = s.AdvancedButtonTip;
        _sideGrid.Columns[0].HeaderText = T("行号", "Line");
        _sideGrid.AccessibleName = T("固定与二次搜索列表", "Pinned and secondary search list");
        _sideClearBtn.Text = s.ClearButton;
        _resultFindAll.Text = s.ResultFindAllButton;
        _resultFindInput.AccessibleName = s.ResultFindPlaceholder;
        _resultFindCase.AccessibleName = s.CaseSensitiveTip;
        _resultFindPrev.AccessibleName = s.ResultFindPrevTip;
        _resultFindNext.AccessibleName = s.ResultFindNextTip;
        _resultFindAll.AccessibleName = s.ResultFindAllTip;
        _resultFindClose.AccessibleName = s.ResultFindCloseTip;
        _searchPaneTip.SetToolTip(_resultFindInput, s.ResultFindPlaceholder);
        _searchPaneTip.SetToolTip(_resultFindCase, s.CaseSensitiveTip);
        _searchPaneTip.SetToolTip(_resultFindPrev, s.ResultFindPrevTip);
        _searchPaneTip.SetToolTip(_resultFindNext, s.ResultFindNextTip);
        _searchPaneTip.SetToolTip(_resultFindAll, s.ResultFindAllTip);
        _searchPaneTip.SetToolTip(_resultFindClose, s.ResultFindCloseTip);
        UpdateSidePane();
        UpdateResultFindUi();
    }

    private void ApplySearchPaneTheme(bool dark)
    {
        var back = dark ? Color.FromArgb(30, 30, 30) : SystemColors.Window;
        var panelBack = dark ? Color.FromArgb(37, 43, 49) : Color.FromArgb(233, 238, 243);
        var ctrlBack = dark ? Color.FromArgb(45, 51, 58) : SystemColors.ControlLightLight;
        var border = dark ? Color.FromArgb(68, 76, 86) : Color.FromArgb(185, 195, 205);
        var fore = dark ? Color.Gainsboro : SystemColors.WindowText;

        _resultsSplit.BackColor = dark ? Color.FromArgb(55, 55, 55) : SystemColors.Control;
        _resultsSplit.Panel1.BackColor = _resultsSplit.Panel2.BackColor = back;
        _sideHeader.BackColor = panelBack;
        _sideTabsFlow.BackColor = panelBack;

        _resultFindBar.BackColor = panelBack;
        _resultFindInput.BackColor = dark ? Color.FromArgb(21, 24, 27) : SystemColors.Window;
        _resultFindInput.ForeColor = fore;
        _resultFindCount.ForeColor = dark ? Color.FromArgb(157, 167, 176) : Color.FromArgb(93, 104, 115);

        foreach (var btn in new ButtonBase[] { _resultFindCase, _resultFindPrev, _resultFindNext, _resultFindAll, _resultFindClose, _sideClearBtn })
        {
            btn.BackColor = ctrlBack;
            btn.ForeColor = fore;
            btn.FlatAppearance.BorderColor = border;
        }
        _resultFindCase.FlatAppearance.CheckedBackColor = dark ? Color.FromArgb(31, 111, 152) : Color.FromArgb(207, 232, 247);

        StyleSideTabButtons();
        _sideGrid.Refresh();
    }

    private void StyleSideTabButtons()
    {
        var activeBack = _dark ? Color.FromArgb(31, 111, 152) : Color.FromArgb(207, 232, 247);
        var activeBorder = _dark ? Color.FromArgb(75, 163, 211) : Color.FromArgb(27, 111, 157);
        var idleBack = _dark ? Color.FromArgb(45, 51, 58) : SystemColors.ControlLightLight;
        var idleBorder = _dark ? Color.FromArgb(68, 76, 86) : Color.FromArgb(185, 195, 205);
        var fore = _dark ? Color.Gainsboro : SystemColors.WindowText;

        var pinnedActive = _current?.ActiveSideTab != SidePaneTab.SecondarySearch;
        _pinnedTabBtn.BackColor = pinnedActive ? activeBack : idleBack;
        _pinnedTabBtn.ForeColor = fore;
        _pinnedTabBtn.FlatAppearance.BorderColor = pinnedActive ? activeBorder : idleBorder;

        _secondaryTabBtn.BackColor = !pinnedActive ? activeBack : idleBack;
        _secondaryTabBtn.ForeColor = fore;
        _secondaryTabBtn.FlatAppearance.BorderColor = !pinnedActive ? activeBorder : idleBorder;
    }
}
