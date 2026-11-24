using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace LogRAM;

public sealed partial class MainWindow
{
    private enum RightPaneTab
    {
        Pinned,
        SecondarySearch
    }

    private List<LogSearchResult> _pinnedResults = new();
    private HashSet<long> _pinnedLineNumbers = new();
    private List<LogSearchResult> _secondarySearchResults = new();
    private string _secondarySearchQuery = string.Empty;
    private RightPaneTab _activeRightPaneTab = RightPaneTab.Pinned;
    private int _pinnedTopIndex;
    private int _secondaryTopIndex;
    private int _sidePageLineCount = MinPageLineCount;
    private bool _isDraggingSideScrollThumb;
    private double _sideScrollDragOffsetY;
    private double _sideMaxContentWidth;
    private bool _sideReserveHScroll;
    private bool _sideHScrollStatePending;
    private ScrollViewer? _sideContentScrollViewer;
    private double _rightPaneWidthRatio = 0.42;

    private bool _isResultFindOpen;
    private bool _isSyncingResultFindUi;
    private List<int> _resultFindMatches = new();
    private int _currentResultFindIndex = -1;
    private readonly DispatcherTimer _resultFindDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _resultFindDirty;
    private bool _searchPinMarkersPending;
    private bool _sidePinMarkersPending;
    private bool _searchFindHighlightPending;

    private void InitializeSearchPane()
    {
        _resultFindDebounceTimer.Tick += (_, _) =>
        {
            _resultFindDebounceTimer.Stop();
            if (_resultFindDirty)
            {
                ExecuteResultFind(navigateToSource: false);
            }
        };

        ApplySearchPaneEditorFont();
        ApplySearchPaneLanguage();
        UpdateRightPaneLayout();
        UpdateResultFindUi();
    }

    private void DisposeSearchPaneTimers()
    {
        _resultFindDebounceTimer.Stop();
        _resultFindDirty = false;
    }

    private void ApplySearchPaneLanguage()
    {
        var s = Loc.S;
        SearchLineNumberBorder.ToolTip = s.PinLineNumberTip;
        SearchPinMenuItem.Header = s.PinLineMenu;
        SidePinMenuItem.Header = s.UnpinLineMenu;
        SideCopyMenuItem.Header = s.MenuCopy;
        SideSelectAllMenuItem.Header = s.MenuSelectAll;
        ClearRightPaneButton.Content = s.ClearButton;

        ResultFindTextBox.ToolTip = s.ResultFindPlaceholder;
        ResultFindCaseButton.ToolTip = s.CaseSensitiveTip;
        ResultFindPrevButton.ToolTip = s.ResultFindPrevTip;
        ResultFindNextButton.ToolTip = s.ResultFindNextTip;
        ResultFindAllButton.Content = s.ResultFindAllButton;
        ResultFindAllButton.ToolTip = s.ResultFindAllTip;
        ResultFindCloseButton.ToolTip = s.ResultFindCloseTip;

        UpdateRightPaneLayout();
        UpdateResultFindUi();
    }

    private void ApplySearchPaneEditorFont()
    {
        SideContentBox.FontFamily = _editorFontFamily;
        SideContentBox.FontSize = _editorFontSize;
        SideLineNumberBox.FontFamily = _editorFontFamily;
        SideLineNumberBox.FontSize = _editorFontSize;

        if (SideContentBox.ActualHeight > 0)
        {
            UpdateSidePageLineCount(SideContentBox.ActualHeight);
        }

        ScheduleSearchPinMarkers();
        ScheduleSidePinMarkers();
        ScheduleSearchFindHighlight();
    }

    private void ApplySearchPaneTheme()
    {
        UpdateRightPaneTabAppearance();
        ScheduleSearchPinMarkers();
        ScheduleSidePinMarkers();
        ScheduleSearchFindHighlight();
    }

    private void SaveSearchPaneTabState(DocumentTab tab)
    {
        _resultFindDebounceTimer.Stop();
        _resultFindDirty = false;
        tab.SecondarySearchQuery = _secondarySearchQuery;
        tab.ActiveRightPaneTab = _activeRightPaneTab;
        tab.PinnedTopIndex = _pinnedTopIndex;
        tab.SecondaryTopIndex = _secondaryTopIndex;
        tab.IsResultFindOpen = _isResultFindOpen;
        tab.ResultFindQuery = ResultFindTextBox.Text;
        tab.ResultFindCaseSensitive = ResultFindCaseButton.IsChecked == true;
        tab.CurrentResultFindIndex = _currentResultFindIndex;
        tab.SideMaxContentWidth = _sideMaxContentWidth;
        tab.SideReserveHScroll = _sideReserveHScroll;
    }

    private void LoadSearchPaneTabState(DocumentTab? tab)
    {
        _resultFindDebounceTimer.Stop();
        _resultFindDirty = false;

        if (tab is null)
        {
            _pinnedResults = new List<LogSearchResult>();
            _pinnedLineNumbers = new HashSet<long>();
            _secondarySearchResults = new List<LogSearchResult>();
            _secondarySearchQuery = string.Empty;
            _activeRightPaneTab = RightPaneTab.Pinned;
            _pinnedTopIndex = 0;
            _secondaryTopIndex = 0;
            _isResultFindOpen = false;
            _resultFindMatches = new List<int>();
            _currentResultFindIndex = -1;
            ResetSideHorizontalScroll();
            _isSyncingResultFindUi = true;
            ResultFindTextBox.Clear();
            ResultFindCaseButton.IsChecked = false;
            _isSyncingResultFindUi = false;
            ResultFindBar.Visibility = Visibility.Collapsed;
            SearchFindHighlightBar.Visibility = Visibility.Collapsed;
            SearchPinMarkersCanvas.Children.Clear();
            SidePinMarkersCanvas.Children.Clear();
            SideLineNumberBox.Clear();
            SideContentBox.Clear();
            UpdateRightPaneLayout();
            UpdateSideScrollAvailability();
            UpdateSideScrollThumb();
            UpdateResultFindUi();
            return;
        }

        _pinnedResults = tab.PinnedResults;
        _pinnedLineNumbers = tab.PinnedLineNumbers;
        _secondarySearchResults = tab.SecondarySearchResults;
        _secondarySearchQuery = tab.SecondarySearchQuery;
        _activeRightPaneTab = tab.ActiveRightPaneTab;
        _pinnedTopIndex = tab.PinnedTopIndex;
        _secondaryTopIndex = tab.SecondaryTopIndex;
        _isResultFindOpen = tab.IsResultFindOpen;
        _resultFindMatches = tab.ResultFindMatches;
        _currentResultFindIndex = tab.CurrentResultFindIndex;
        _sideMaxContentWidth = tab.SideMaxContentWidth;
        _sideReserveHScroll = tab.SideReserveHScroll;
        SideContentBox.HorizontalScrollBarVisibility = _sideReserveHScroll
            ? ScrollBarVisibility.Visible
            : ScrollBarVisibility.Auto;

        _isSyncingResultFindUi = true;
        ResultFindTextBox.Text = tab.ResultFindQuery;
        ResultFindCaseButton.IsChecked = tab.ResultFindCaseSensitive;
        _isSyncingResultFindUi = false;
        ResultFindBar.Visibility = _isResultFindOpen ? Visibility.Visible : Visibility.Collapsed;

        UpdateRightPaneLayout();
        RefreshSideTextBoxes();
        UpdateSideScrollAvailability();
        UpdateSideScrollThumb();
        UpdateResultFindUi();
        ScheduleSearchPinMarkers();
        ScheduleSearchFindHighlight();
    }

    private void ResetSecondarySearchForNewSearch()
    {
        _resultFindDebounceTimer.Stop();
        _resultFindDirty = false;
        _resultFindMatches.Clear();
        _currentResultFindIndex = -1;
        _secondarySearchResults.Clear();
        _secondarySearchQuery = string.Empty;
        _secondaryTopIndex = 0;
        SearchFindHighlightBar.Visibility = Visibility.Collapsed;

        UpdateRightPaneLayout();
        RefreshSideTextBoxes();
        UpdateSideScrollAvailability();
        UpdateSideScrollThumb();
        UpdateResultFindUi();
    }

    private void OnPrimarySearchCompleted()
    {
        if (_isResultFindOpen && !string.IsNullOrEmpty(ResultFindTextBox.Text))
        {
            ExecuteResultFind(navigateToSource: false);
            return;
        }

        UpdateResultFindUi();
        ScheduleSearchPinMarkers();
    }

    private void OnDocumentReloaded(ILogDocument newDocument)
    {
        ResetSecondarySearchForNewSearch();

        if (_pinnedResults.Count > 0)
        {
            var localDoc = newDocument as LogFileDocument;
            for (var i = _pinnedResults.Count - 1; i >= 0; i--)
            {
                var lineNumber = _pinnedResults[i].LineNumber;
                if (lineNumber > newDocument.LineCount)
                {
                    _pinnedLineNumbers.Remove(lineNumber);
                    _pinnedResults.RemoveAt(i);
                    continue;
                }

                if (localDoc is not null)
                {
                    _pinnedResults[i] = new LogSearchResult(
                        lineNumber,
                        localDoc.GetOffsetForLine(lineNumber),
                        localDoc.GetLineText(lineNumber));
                }
            }
        }

        UpdateRightPaneLayout();
        RefreshSideTextBoxes();
        UpdateSideScrollAvailability();
        UpdateSideScrollThumb();
        ScheduleSearchPinMarkers();
    }

    private void RefreshLiveTailPinnedLine(long firstChangedLine)
    {
        if (!_pinnedLineNumbers.Contains(firstChangedLine) || _document is not LogFileDocument localDoc)
        {
            return;
        }

        if (firstChangedLine < 1 || firstChangedLine > localDoc.LineCount)
        {
            return;
        }

        for (var i = 0; i < _pinnedResults.Count; i++)
        {
            if (_pinnedResults[i].LineNumber == firstChangedLine)
            {
                _pinnedResults[i] = new LogSearchResult(
                    firstChangedLine,
                    localDoc.GetOffsetForLine(firstChangedLine),
                    localDoc.GetLineText(firstChangedLine));
                if (_activeRightPaneTab == RightPaneTab.Pinned)
                {
                    RefreshSideTextBoxes();
                }

                break;
            }
        }
    }

    private void RefreshResultFindAfterLiveBatch()
    {
        if (_isResultFindOpen && !string.IsNullOrEmpty(ResultFindTextBox.Text) && !_isSearching)
        {
            var previousResultIndex = _currentResultFindIndex >= 0 && _currentResultFindIndex < _resultFindMatches.Count
                ? _resultFindMatches[_currentResultFindIndex]
                : -1;
            ExecuteResultFind(navigateToSource: false, preferredResultIndex: previousResultIndex);
        }
    }

    private bool IsFocusInSearchResultsArea()
    {
        return Keyboard.FocusedElement is DependencyObject focused &&
               IsDescendantOf(focused, SearchResultsHostGrid);
    }

    private static bool IsDescendantOf(DependencyObject? element, DependencyObject ancestor)
    {
        var current = element;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private bool HandleSearchPaneKeyDown(KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.F)
        {
            if (IsFocusInSearchResultsArea())
            {
                e.Handled = true;
                OpenResultFindBar();
                return true;
            }

            return false;
        }

        if (e.Key == Key.Escape && _isResultFindOpen && IsFocusInSearchResultsArea())
        {
            e.Handled = true;
            CloseResultFindBar();
            return true;
        }

        if (e.Key == Key.F3 && _isResultFindOpen && _resultFindMatches.Count > 0 && IsFocusInSearchResultsArea())
        {
            e.Handled = true;
            NavigateResultFindMatch((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ? -1 : 1);
            return true;
        }

        return false;
    }

    internal void OpenResultFindBar()
    {
        _isResultFindOpen = true;
        ResultFindBar.Visibility = Visibility.Visible;

        if (SearchContentBox.SelectionLength > 0 && SearchContentBox.SelectionLength <= 128)
        {
            var selected = SearchContentBox.SelectedText;
            if (!selected.Contains('\n') && !selected.Contains('\r'))
            {
                _isSyncingResultFindUi = true;
                ResultFindTextBox.Text = selected;
                _isSyncingResultFindUi = false;
            }
        }

        ResultFindTextBox.Focus();
        ResultFindTextBox.SelectAll();

        if (!string.IsNullOrEmpty(ResultFindTextBox.Text))
        {
            ExecuteResultFind(navigateToSource: false);
        }
        else
        {
            UpdateResultFindUi();
        }
    }

    internal void CloseResultFindBar()
    {
        _resultFindDebounceTimer.Stop();
        _resultFindDirty = false;
        _isResultFindOpen = false;
        ResultFindBar.Visibility = Visibility.Collapsed;
        SearchFindHighlightBar.Visibility = Visibility.Collapsed;
        SearchContentBox.Focus();
    }

    private void ResultFindTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSyncingResultFindUi)
        {
            return;
        }

        if (_searchResults.Count > 5000)
        {
            _resultFindDirty = true;
            _resultFindDebounceTimer.Stop();
            _resultFindDebounceTimer.Start();
            return;
        }

        _resultFindDebounceTimer.Stop();
        _resultFindDirty = false;
        ExecuteResultFind(navigateToSource: false);
    }

    private void ResultFindTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            NavigateResultFindMatch((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ? -1 : 1);
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseResultFindBar();
            return;
        }

        if (e.Key == Key.F3)
        {
            e.Handled = true;
            NavigateResultFindMatch((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ? -1 : 1);
        }
    }

    private void ResultFindCaseButton_Changed(object sender, RoutedEventArgs e)
    {
        if (_isSyncingResultFindUi)
        {
            return;
        }

        _resultFindDebounceTimer.Stop();
        _resultFindDirty = false;
        ExecuteResultFind(navigateToSource: false);
    }

    private void ResultFindPrevButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateResultFindMatch(-1);
    }

    private void ResultFindNextButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateResultFindMatch(1);
    }

    private void ResultFindAllButton_Click(object sender, RoutedEventArgs e)
    {
        CommitSecondarySearchAll();
    }

    private void ResultFindCloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseResultFindBar();
    }

    private void FlushPendingResultFind()
    {
        if (_resultFindDirty)
        {
            _resultFindDebounceTimer.Stop();
            ExecuteResultFind(navigateToSource: false);
        }
    }

    private void ExecuteResultFind(bool navigateToSource, int preferredResultIndex = -1)
    {
        _resultFindDirty = false;
        _resultFindDebounceTimer.Stop();

        var query = ResultFindTextBox.Text;
        _resultFindMatches.Clear();
        _currentResultFindIndex = -1;

        if (string.IsNullOrEmpty(query) || _searchResults.Count == 0)
        {
            SearchFindHighlightBar.Visibility = Visibility.Collapsed;
            UpdateResultFindUi();
            return;
        }

        var caseSensitive = ResultFindCaseButton.IsChecked == true;
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        LogFileDocument.LineTextMatcher? matcher = _document is LogFileDocument localDoc
            ? localDoc.CreateLineTextMatcher(query, caseSensitive)
            : null;

        for (var i = 0; i < _searchResults.Count; i++)
        {
            if (_searchResults[i].MatchesText(matcher, query, comparison))
            {
                _resultFindMatches.Add(i);
            }
        }

        if (_resultFindMatches.Count == 0)
        {
            SearchFindHighlightBar.Visibility = Visibility.Collapsed;
            UpdateResultFindUi();
            return;
        }

        var chosen = -1;
        if (preferredResultIndex >= 0)
        {
            chosen = _resultFindMatches.BinarySearch(preferredResultIndex);
            if (chosen < 0)
            {
                chosen = -1;
            }
        }

        if (chosen < 0)
        {
            chosen = 0;
            for (var i = 0; i < _resultFindMatches.Count; i++)
            {
                if (_resultFindMatches[i] >= _searchTopIndex)
                {
                    chosen = i;
                    break;
                }
            }
        }

        _currentResultFindIndex = chosen;
        RevealCurrentResultFindMatch(navigateToSource);
        UpdateResultFindUi();
    }

    internal void NavigateResultFindMatch(int delta)
    {
        FlushPendingResultFind();
        if (_resultFindMatches.Count == 0)
        {
            return;
        }

        if (_currentResultFindIndex < 0 || _currentResultFindIndex >= _resultFindMatches.Count)
        {
            _currentResultFindIndex = delta >= 0 ? 0 : _resultFindMatches.Count - 1;
        }
        else
        {
            _currentResultFindIndex = (_currentResultFindIndex + delta + _resultFindMatches.Count) % _resultFindMatches.Count;
        }

        RevealCurrentResultFindMatch(navigateToSource: true);
        UpdateResultFindUi();
    }

    private void RevealCurrentResultFindMatch(bool navigateToSource)
    {
        if (_currentResultFindIndex < 0 || _currentResultFindIndex >= _resultFindMatches.Count)
        {
            SearchFindHighlightBar.Visibility = Visibility.Collapsed;
            return;
        }

        var resultIndex = _resultFindMatches[_currentResultFindIndex];
        if (resultIndex < 0 || resultIndex >= _searchResults.Count)
        {
            return;
        }

        _selectedSearchResultIndex = resultIndex;
        if (resultIndex < _searchTopIndex || resultIndex >= _searchTopIndex + _searchPageLineCount)
        {
            var maxTopIndex = Math.Max(0, _searchResults.Count - _searchPageLineCount);
            _searchTopIndex = Math.Clamp(resultIndex - _searchPageLineCount / 2, 0, maxTopIndex);
            RefreshSearchTextBoxes();
            UpdateSearchScrollThumb();
        }
        else
        {
            ScheduleSearchFindHighlight();
        }

        if (navigateToSource)
        {
            NavigateToSearchResult(_searchResults[resultIndex]);
        }
    }

    internal void CommitSecondarySearchAll()
    {
        FlushPendingResultFind();
        if (_resultFindMatches.Count == 0)
        {
            return;
        }

        _secondarySearchResults.Clear();
        foreach (var index in _resultFindMatches)
        {
            if (index >= 0 && index < _searchResults.Count)
            {
                _secondarySearchResults.Add(_searchResults[index].ToSnapshot());
            }
        }

        _secondarySearchQuery = ResultFindTextBox.Text;
        _secondaryTopIndex = 0;
        _activeRightPaneTab = RightPaneTab.SecondarySearch;
        ResetSideHorizontalScroll();
        UpdateRightPaneLayout();
        RefreshSideTextBoxes();
        UpdateSideScrollAvailability();
        UpdateSideScrollThumb();
    }

    private void UpdateResultFindUi()
    {
        var query = ResultFindTextBox.Text;
        var hasMatches = _resultFindMatches.Count > 0;
        if (string.IsNullOrEmpty(query))
        {
            ResultFindCountTextBlock.Text = string.Empty;
        }
        else
        {
            var current = hasMatches ? _currentResultFindIndex + 1 : 0;
            ResultFindCountTextBlock.Text = Loc.S.ResultFindMatchCount(current, _resultFindMatches.Count);
        }

        ResultFindPrevButton.IsEnabled = hasMatches;
        ResultFindNextButton.IsEnabled = hasMatches;
        ResultFindAllButton.IsEnabled = hasMatches;
    }

    private void ScheduleSearchFindHighlight()
    {
        if (!_isResultFindOpen || _currentResultFindIndex < 0 || _currentResultFindIndex >= _resultFindMatches.Count)
        {
            SearchFindHighlightBar.Visibility = Visibility.Collapsed;
            return;
        }

        if (_searchFindHighlightPending)
        {
            return;
        }

        _searchFindHighlightPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ApplySearchFindHighlight));
    }

    private void ApplySearchFindHighlight()
    {
        _searchFindHighlightPending = false;
        SearchFindHighlightBar.Visibility = Visibility.Collapsed;

        if (!_isResultFindOpen || _currentResultFindIndex < 0 || _currentResultFindIndex >= _resultFindMatches.Count)
        {
            return;
        }

        var resultIndex = _resultFindMatches[_currentResultFindIndex];
        var lineInView = resultIndex - _searchTopIndex;
        if (lineInView < 0 || lineInView >= SearchContentBox.LineCount)
        {
            return;
        }

        var lineStart = SearchContentBox.GetCharacterIndexFromLineIndex(lineInView);
        if (lineStart < 0)
        {
            return;
        }

        var rect = SearchContentBox.GetRectFromCharacterIndex(lineStart);
        if (rect.IsEmpty || double.IsInfinity(rect.Top) || double.IsNaN(rect.Top))
        {
            return;
        }

        var height = rect.Height > 0 ? rect.Height : GetSearchContentLineHeight();
        SearchFindHighlightBar.Margin = new Thickness(0, rect.Top, 0, 0);
        SearchFindHighlightBar.Height = height;
        SearchFindHighlightBar.Visibility = Visibility.Visible;

        var query = ResultFindTextBox.Text;
        if (!string.IsNullOrEmpty(query))
        {
            var lineLength = SearchContentBox.GetLineLength(lineInView);
            var text = SearchContentBox.Text;
            while (lineLength > 0 &&
                   lineStart + lineLength - 1 < text.Length &&
                   (text[lineStart + lineLength - 1] == '\n' || text[lineStart + lineLength - 1] == '\r'))
            {
                lineLength--;
            }

            var comparison = ResultFindCaseButton.IsChecked == true
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            var matchOffset = lineLength > 0
                ? text.IndexOf(query, lineStart, lineLength, comparison)
                : -1;
            if (matchOffset >= 0)
            {
                SearchContentBox.Select(matchOffset, query.Length);
            }
            else
            {
                SelectBoxLine(SearchContentBox, lineInView);
            }
        }
    }

    internal void TogglePinResult(LogSearchResult item, bool switchToPinnedTab)
    {
        if (_pinnedLineNumbers.Remove(item.LineNumber))
        {
            for (var i = 0; i < _pinnedResults.Count; i++)
            {
                if (_pinnedResults[i].LineNumber == item.LineNumber)
                {
                    _pinnedResults.RemoveAt(i);
                    break;
                }
            }
        }
        else
        {
            _pinnedLineNumbers.Add(item.LineNumber);
            var snapshot = item.ToSnapshot();
            var insertAt = 0;
            while (insertAt < _pinnedResults.Count && _pinnedResults[insertAt].LineNumber < snapshot.LineNumber)
            {
                insertAt++;
            }

            _pinnedResults.Insert(insertAt, snapshot);
            if (switchToPinnedTab)
            {
                _activeRightPaneTab = RightPaneTab.Pinned;
            }
        }

        UpdateRightPaneLayout();
        RefreshSideTextBoxes();
        UpdateSideScrollAvailability();
        UpdateSideScrollThumb();
        ScheduleSearchPinMarkers();
        ScheduleSidePinMarkers();
    }

    private void SearchLineNumberBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isRefreshingLive || _searchResults.Count == 0)
        {
            return;
        }

        var visibleCount = Math.Min(_searchPageLineCount, Math.Max(0, _searchResults.Count - _searchTopIndex));
        var lineInView = GetLineIndexAtY(SearchLineNumberBox, e.GetPosition(SearchLineNumberBox).Y, visibleCount);
        if (lineInView < 0)
        {
            return;
        }

        var resultIndex = _searchTopIndex + lineInView;
        if (resultIndex < 0 || resultIndex >= _searchResults.Count)
        {
            return;
        }

        TogglePinResult(_searchResults[resultIndex], switchToPinnedTab: true);
        if (!IsDescendantOf(Keyboard.FocusedElement as DependencyObject, ResultFindBar))
        {
            SearchContentBox.Focus();
        }

        e.Handled = true;
    }

    private void SideLineNumberBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isRefreshingLive)
        {
            return;
        }

        var list = GetActiveSideResults();
        if (list.Count == 0)
        {
            return;
        }

        var topIndex = GetActiveSideTopIndex();
        var visibleCount = Math.Min(_sidePageLineCount, Math.Max(0, list.Count - topIndex));
        var lineInView = GetLineIndexAtY(SideLineNumberBox, e.GetPosition(SideLineNumberBox).Y, visibleCount);
        if (lineInView < 0)
        {
            return;
        }

        var itemIndex = topIndex + lineInView;
        if (itemIndex < 0 || itemIndex >= list.Count)
        {
            return;
        }

        TogglePinResult(list[itemIndex], switchToPinnedTab: false);
        e.Handled = true;
    }

    private void SearchContentBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var visibleCount = Math.Min(_searchPageLineCount, Math.Max(0, _searchResults.Count - _searchTopIndex));
        var lineInView = GetLineIndexAtY(SearchContentBox, Mouse.GetPosition(SearchContentBox).Y, visibleCount);
        if (lineInView < 0 && SearchContentBox.LineCount > 0)
        {
            lineInView = SearchContentBox.GetLineIndexFromCharacterIndex(SearchContentBox.CaretIndex);
        }

        var resultIndex = lineInView >= 0 ? _searchTopIndex + lineInView : -1;
        if (resultIndex >= 0 && resultIndex < _searchResults.Count)
        {
            var item = _searchResults[resultIndex];
            SearchPinMenuItem.Tag = item;
            SearchPinMenuItem.IsEnabled = true;
            SearchPinMenuItem.Header = _pinnedLineNumbers.Contains(item.LineNumber)
                ? Loc.S.UnpinLineMenu
                : Loc.S.PinLineMenu;
        }
        else
        {
            SearchPinMenuItem.Tag = null;
            SearchPinMenuItem.IsEnabled = false;
            SearchPinMenuItem.Header = Loc.S.PinLineMenu;
        }
    }

    private void SearchPinMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (SearchPinMenuItem.Tag is LogSearchResult item)
        {
            TogglePinResult(item, switchToPinnedTab: true);
        }
    }

    private void SideContentBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var list = GetActiveSideResults();
        var topIndex = GetActiveSideTopIndex();
        var visibleCount = Math.Min(_sidePageLineCount, Math.Max(0, list.Count - topIndex));
        var lineInView = GetLineIndexAtY(SideContentBox, Mouse.GetPosition(SideContentBox).Y, visibleCount);
        if (lineInView < 0 && SideContentBox.LineCount > 0)
        {
            lineInView = SideContentBox.GetLineIndexFromCharacterIndex(SideContentBox.CaretIndex);
        }

        var itemIndex = lineInView >= 0 ? topIndex + lineInView : -1;
        if (itemIndex >= 0 && itemIndex < list.Count)
        {
            var item = list[itemIndex];
            SidePinMenuItem.Tag = item;
            SidePinMenuItem.IsEnabled = true;
            SidePinMenuItem.Header = _pinnedLineNumbers.Contains(item.LineNumber)
                ? Loc.S.UnpinLineMenu
                : Loc.S.PinLineMenu;
        }
        else
        {
            SidePinMenuItem.Tag = null;
            SidePinMenuItem.IsEnabled = false;
            SidePinMenuItem.Header = Loc.S.PinLineMenu;
        }
    }

    private void SidePinMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (SidePinMenuItem.Tag is LogSearchResult item)
        {
            TogglePinResult(item, switchToPinnedTab: false);
        }
    }

    private void PinnedTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pinnedResults.Count == 0)
        {
            UpdateRightPaneLayout();
            return;
        }

        _activeRightPaneTab = RightPaneTab.Pinned;
        ResetSideHorizontalScroll();
        UpdateRightPaneLayout();
        RefreshSideTextBoxes();
        UpdateSideScrollAvailability();
        UpdateSideScrollThumb();
    }

    private void SecondarySearchTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (_secondarySearchResults.Count == 0)
        {
            UpdateRightPaneLayout();
            return;
        }

        _activeRightPaneTab = RightPaneTab.SecondarySearch;
        ResetSideHorizontalScroll();
        UpdateRightPaneLayout();
        RefreshSideTextBoxes();
        UpdateSideScrollAvailability();
        UpdateSideScrollThumb();
    }

    private void ClearRightPaneButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeRightPaneTab == RightPaneTab.Pinned)
        {
            _pinnedResults.Clear();
            _pinnedLineNumbers.Clear();
            _pinnedTopIndex = 0;
            ScheduleSearchPinMarkers();
        }
        else
        {
            _secondarySearchResults.Clear();
            _secondarySearchQuery = string.Empty;
            _secondaryTopIndex = 0;
        }

        ResetSideHorizontalScroll();
        UpdateRightPaneLayout();
        RefreshSideTextBoxes();
        UpdateSideScrollAvailability();
        UpdateSideScrollThumb();
    }

    private void SearchRightSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var leftWidth = SearchLeftColumn.ActualWidth;
        var rightWidth = SearchRightColumn.ActualWidth;
        var total = leftWidth + rightWidth;
        if (total > 0)
        {
            _rightPaneWidthRatio = Math.Clamp(rightWidth / total, 0.2, 0.75);
        }
    }

    private void UpdateRightPaneLayout()
    {
        var hasPinned = _pinnedResults.Count > 0;
        var hasSecondary = _secondarySearchResults.Count > 0;
        var hasAny = hasPinned || hasSecondary;

        if (!hasAny)
        {
            SearchRightSplitter.Visibility = Visibility.Collapsed;
            SearchRightPane.Visibility = Visibility.Collapsed;
            SearchRightSplitterColumn.Width = new GridLength(0);
            SearchRightColumn.MinWidth = 0;
            SearchRightColumn.Width = new GridLength(0);
            SearchLeftColumn.Width = new GridLength(1, GridUnitType.Star);
            return;
        }

        if (_activeRightPaneTab == RightPaneTab.Pinned && !hasPinned)
        {
            _activeRightPaneTab = RightPaneTab.SecondarySearch;
        }
        else if (_activeRightPaneTab == RightPaneTab.SecondarySearch && !hasSecondary)
        {
            _activeRightPaneTab = RightPaneTab.Pinned;
        }

        PinnedTabButton.Visibility = hasPinned ? Visibility.Visible : Visibility.Collapsed;
        PinnedTabButton.Content = Loc.S.PinnedTab(_pinnedResults.Count);
        PinnedTabButton.IsChecked = _activeRightPaneTab == RightPaneTab.Pinned;

        SecondarySearchTabButton.Visibility = hasSecondary ? Visibility.Visible : Visibility.Collapsed;
        SecondarySearchTabButton.Content = Loc.S.SecondarySearchTab(_secondarySearchResults.Count);
        SecondarySearchTabButton.IsChecked = _activeRightPaneTab == RightPaneTab.SecondarySearch;
        SecondarySearchTabButton.ToolTip = string.IsNullOrEmpty(_secondarySearchQuery) ? null : _secondarySearchQuery;

        UpdateRightPaneTabAppearance();

        if (SearchRightPane.Visibility != Visibility.Visible)
        {
            SearchRightSplitter.Visibility = Visibility.Visible;
            SearchRightPane.Visibility = Visibility.Visible;
            SearchRightSplitterColumn.Width = new GridLength(5);
            SearchRightColumn.MinWidth = 180;
            SearchLeftColumn.Width = new GridLength(1.0 - _rightPaneWidthRatio, GridUnitType.Star);
            SearchRightColumn.Width = new GridLength(_rightPaneWidthRatio, GridUnitType.Star);
        }
    }

    private void UpdateRightPaneTabAppearance()
    {
        var isPinned = _activeRightPaneTab == RightPaneTab.Pinned;
        ClearRightPaneButton.ToolTip = isPinned ? Loc.S.ClearPinnedTip : Loc.S.ClearSecondaryTip;
        SideLineNumberBorder.ToolTip = isPinned ? Loc.S.UnpinLineNumberTip : Loc.S.PinLineNumberTip;
        SideLineNumberBox.Foreground = (Brush)FindResource(isPinned ? "AccentBrush" : "WarningBrush");
    }

    private List<LogSearchResult> GetActiveSideResults()
    {
        return _activeRightPaneTab == RightPaneTab.Pinned ? _pinnedResults : _secondarySearchResults;
    }

    private int GetActiveSideTopIndex()
    {
        return _activeRightPaneTab == RightPaneTab.Pinned ? _pinnedTopIndex : _secondaryTopIndex;
    }

    private void SetActiveSideTopIndex(int value)
    {
        if (_activeRightPaneTab == RightPaneTab.Pinned)
        {
            _pinnedTopIndex = value;
        }
        else
        {
            _secondaryTopIndex = value;
        }
    }

    private void ClampSideTopIndex()
    {
        var list = GetActiveSideResults();
        var maxTopIndex = Math.Max(0, list.Count - _sidePageLineCount);
        SetActiveSideTopIndex((int)Math.Clamp((long)GetActiveSideTopIndex(), 0, maxTopIndex));
    }

    private void RefreshSideTextBoxes()
    {
        var list = GetActiveSideResults();
        if (list.Count == 0 || SearchRightPane.Visibility != Visibility.Visible)
        {
            if (SideContentBox.Text.Length > 0 || SideLineNumberBox.Text.Length > 0)
            {
                SideLineNumberBox.Clear();
                SideContentBox.Clear();
            }

            SidePinMarkersCanvas.Children.Clear();
            return;
        }

        ClampSideTopIndex();
        var topIndex = GetActiveSideTopIndex();
        var count = Math.Min(_sidePageLineCount, list.Count - topIndex);
        var numbers = new StringBuilder();
        var content = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                numbers.Append('\n');
                content.Append('\n');
            }

            var result = list[topIndex + i];
            numbers.Append(result.LineNumber);
            content.Append(result.Text);
        }

        SideLineNumberBox.Text = numbers.ToString();
        SideContentBox.Text = content.ToString();
        ScheduleSideHorizontalScrollState();
        ScheduleSidePinMarkers();
    }

    private void SideContentBox_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateSidePageLineCount(e.NewSize.Height);
    }

    private void UpdateSidePageLineCount(double viewportHeight)
    {
        var lineHeight = GetSideContentLineHeight();
        var reserve = _sideReserveHScroll ? HorizontalScrollBarHeight : 0;
        var usableHeight = viewportHeight - SideContentBox.Padding.Top - SideContentBox.Padding.Bottom - reserve;
        var newCount = Math.Max(MinPageLineCount, (int)(usableHeight / lineHeight));
        if (newCount == _sidePageLineCount)
        {
            return;
        }

        _sidePageLineCount = newCount;
        UpdateSideScrollAvailability();
        ClampSideTopIndex();
        RefreshSideTextBoxes();
        UpdateSideScrollThumb();
    }

    private double GetSideContentLineHeight()
    {
        var lineHeight = SideContentBox.FontFamily.LineSpacing * SideContentBox.FontSize;
        return lineHeight > 0 ? lineHeight : LogLineHeight;
    }

    private void SideContentBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isRefreshingLive || SideContentBox.SelectionLength > 0)
        {
            return;
        }

        var list = GetActiveSideResults();
        if (list.Count == 0)
        {
            return;
        }

        var lineInView = SideContentBox.GetLineIndexFromCharacterIndex(SideContentBox.CaretIndex);
        if (lineInView < 0)
        {
            return;
        }

        var itemIndex = GetActiveSideTopIndex() + lineInView;
        if (itemIndex < 0 || itemIndex >= list.Count)
        {
            return;
        }

        SelectBoxLine(SideContentBox, lineInView);
        NavigateToSearchResult(list[itemIndex]);
    }

    private void SideResultsGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!CanScrollSide() || e.Delta == 0)
        {
            return;
        }

        var lineDelta = (long)Math.Round(-e.Delta / 120.0 * LogWheelLinesPerDetent);
        if (lineDelta == 0)
        {
            lineDelta = e.Delta > 0 ? -1 : 1;
        }

        var list = GetActiveSideResults();
        var maxTopIndex = Math.Max(0, list.Count - _sidePageLineCount);
        var current = GetActiveSideTopIndex();
        var target = (int)Math.Clamp((long)current + lineDelta, 0, maxTopIndex);
        if (target != current)
        {
            SetActiveSideTopIndex(target);
            RefreshSideTextBoxes();
            UpdateSideScrollThumb();
        }

        e.Handled = true;
    }

    private void SideScrollTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanScrollSide())
        {
            return;
        }

        var pointerY = e.GetPosition(SideScrollTrack).Y;
        var thumbTop = SideScrollThumbTransform.Y;
        var thumbHeight = GetSideScrollThumbHeight();
        _sideScrollDragOffsetY = pointerY >= thumbTop && pointerY <= thumbTop + thumbHeight
            ? pointerY - thumbTop
            : thumbHeight / 2;
        _isDraggingSideScrollThumb = true;
        SideScrollTrack.CaptureMouse();
        SetSideScrollFromThumbTop(pointerY - _sideScrollDragOffsetY);
        e.Handled = true;
    }

    private void SideScrollTrack_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingSideScrollThumb)
        {
            return;
        }

        SetSideScrollFromThumbTop(e.GetPosition(SideScrollTrack).Y - _sideScrollDragOffsetY);
        e.Handled = true;
    }

    private void SideScrollTrack_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingSideScrollThumb)
        {
            return;
        }

        _isDraggingSideScrollThumb = false;
        SideScrollTrack.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void SideScrollTrack_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _isDraggingSideScrollThumb = false;
    }

    private void SideScrollTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateSideScrollThumb();
    }

    private void SetSideScrollFromThumbTop(double requestedTop)
    {
        if (!CanScrollSide() || SideScrollTrack.ActualHeight <= 0)
        {
            return;
        }

        var list = GetActiveSideResults();
        var thumbHeight = GetSideScrollThumbHeight();
        var maxThumbTop = Math.Max(0, SideScrollTrack.ActualHeight - thumbHeight);
        var thumbTop = Math.Clamp(requestedTop, 0, maxThumbTop);
        var maxValue = Math.Max(1, list.Count - _sidePageLineCount);
        var target = maxThumbTop <= 0
            ? 0
            : (int)Math.Clamp(Math.Round(thumbTop / maxThumbTop * maxValue), 0, maxValue);

        if (target == GetActiveSideTopIndex())
        {
            UpdateSideScrollThumb();
            return;
        }

        SetActiveSideTopIndex(target);
        RefreshSideTextBoxes();
        UpdateSideScrollThumb();
    }

    private bool CanScrollSide()
    {
        return GetActiveSideResults().Count > _sidePageLineCount;
    }

    private void UpdateSideScrollAvailability()
    {
        var canScroll = CanScrollSide();
        SideScrollTrack.IsHitTestVisible = canScroll;
        SideScrollTrack.Opacity = canScroll ? 1 : 0.35;
        SideScrollThumb.Visibility = canScroll ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSideScrollThumb()
    {
        var list = GetActiveSideResults();
        if (!CanScrollSide() || SideScrollTrack.ActualHeight <= 0)
        {
            SideScrollThumbTransform.Y = 0;
            return;
        }

        var trackHeight = SideScrollTrack.ActualHeight;
        var thumbHeight = Math.Clamp(
            trackHeight * _sidePageLineCount / list.Count,
            Math.Min(SideScrollThumb.MinHeight, trackHeight),
            trackHeight);
        var maxThumbTop = Math.Max(0, trackHeight - thumbHeight);
        var maxValue = Math.Max(1, list.Count - _sidePageLineCount);
        var value = Math.Clamp((long)GetActiveSideTopIndex(), 0, maxValue);

        SideScrollThumb.Height = thumbHeight;
        SideScrollThumbTransform.Y = maxThumbTop * value / maxValue;
    }

    private double GetSideScrollThumbHeight()
    {
        return SideScrollThumb.ActualHeight > 0 ? SideScrollThumb.ActualHeight : SideScrollThumb.Height;
    }

    private void ResetSideHorizontalScroll()
    {
        _sideMaxContentWidth = 0;
        _sideReserveHScroll = false;
        SideContentBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
    }

    private void ScheduleSideHorizontalScrollState()
    {
        if (_sideHScrollStatePending)
        {
            return;
        }

        _sideHScrollStatePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateSideHorizontalScrollState));
    }

    private void UpdateSideHorizontalScrollState()
    {
        _sideHScrollStatePending = false;
        _sideContentScrollViewer ??= FindContentScrollViewer(SideContentBox);
        var scrollViewer = _sideContentScrollViewer;
        if (scrollViewer is null)
        {
            return;
        }

        if (scrollViewer.ExtentWidth > _sideMaxContentWidth)
        {
            _sideMaxContentWidth = scrollViewer.ExtentWidth;
        }

        var needScroll = _sideMaxContentWidth > scrollViewer.ViewportWidth + 0.5;
        SideContentBox.HorizontalScrollBarVisibility =
            needScroll ? ScrollBarVisibility.Visible : ScrollBarVisibility.Auto;

        if (needScroll != _sideReserveHScroll)
        {
            _sideReserveHScroll = needScroll;
            UpdateSidePageLineCount(SideContentBox.ActualHeight);
        }
    }

    private void ScheduleSearchPinMarkers()
    {
        if (_searchPinMarkersPending)
        {
            return;
        }

        _searchPinMarkersPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ApplySearchPinMarkers));
    }

    private void ApplySearchPinMarkers()
    {
        _searchPinMarkersPending = false;
        RenderPinMarkers(
            SearchPinMarkersCanvas,
            SearchLineNumberBox,
            _searchResults,
            _searchTopIndex,
            _searchPageLineCount);
    }

    private void ScheduleSidePinMarkers()
    {
        if (_sidePinMarkersPending)
        {
            return;
        }

        _sidePinMarkersPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ApplySidePinMarkers));
    }

    private void ApplySidePinMarkers()
    {
        _sidePinMarkersPending = false;
        if (SearchRightPane.Visibility != Visibility.Visible)
        {
            SidePinMarkersCanvas.Children.Clear();
            return;
        }

        RenderPinMarkers(
            SidePinMarkersCanvas,
            SideLineNumberBox,
            GetActiveSideResults(),
            GetActiveSideTopIndex(),
            _sidePageLineCount);
    }

    private void RenderPinMarkers(
        Canvas canvas,
        TextBox lineNumberBox,
        IReadOnlyList<LogSearchResult> items,
        int topIndex,
        int pageLineCount)
    {
        canvas.Children.Clear();
        if (_pinnedLineNumbers.Count == 0 || items.Count == 0 || lineNumberBox.LineCount <= 0)
        {
            return;
        }

        var accent = (Brush)FindResource("AccentBrush");
        var width = canvas.ActualWidth > 0 ? canvas.ActualWidth : lineNumberBox.ActualWidth;
        var count = Math.Min(Math.Min(pageLineCount, items.Count - topIndex), lineNumberBox.LineCount);
        for (var i = 0; i < count; i++)
        {
            var result = items[topIndex + i];
            if (!_pinnedLineNumbers.Contains(result.LineNumber))
            {
                continue;
            }

            var charIndex = lineNumberBox.GetCharacterIndexFromLineIndex(i);
            if (charIndex < 0)
            {
                continue;
            }

            var rect = lineNumberBox.GetRectFromCharacterIndex(charIndex);
            if (rect.IsEmpty || double.IsNaN(rect.Top) || double.IsInfinity(rect.Top))
            {
                continue;
            }

            var height = rect.Height > 0 ? rect.Height : GetSearchContentLineHeight();
            if (width > 0)
            {
                var tint = new Rectangle
                {
                    Width = width,
                    Height = height,
                    Fill = accent,
                    Opacity = 0.16
                };
                Canvas.SetLeft(tint, 0);
                Canvas.SetTop(tint, rect.Top);
                canvas.Children.Add(tint);
            }

            var bar = new Rectangle
            {
                Width = 3,
                Height = height,
                Fill = accent
            };
            Canvas.SetLeft(bar, 0);
            Canvas.SetTop(bar, rect.Top);
            canvas.Children.Add(bar);
        }
    }

    private static int GetLineIndexAtY(TextBox box, double y, int visibleCount)
    {
        if (visibleCount <= 0 || box.LineCount <= 0)
        {
            return -1;
        }

        var count = Math.Min(visibleCount, box.LineCount);
        for (var i = 0; i < count; i++)
        {
            var charIndex = box.GetCharacterIndexFromLineIndex(i);
            if (charIndex < 0)
            {
                continue;
            }

            var rect = box.GetRectFromCharacterIndex(charIndex);
            if (rect.IsEmpty || double.IsNaN(rect.Top) || double.IsInfinity(rect.Top))
            {
                continue;
            }

            var height = rect.Height > 0 ? rect.Height : 16;
            if (y >= rect.Top && y < rect.Top + height)
            {
                return i;
            }
        }

        return -1;
    }
}
