using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace LogRAM;

public sealed partial class MainWindow : Window
{
    private const double LogLineHeight = 20;
    private const int MinPageLineCount = 1;
    private const int LogWheelLinesPerDetent = 3;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const double DefaultEditorFontSize = 12;
    private const string DefaultEditorFontFamily = "Consolas";

    private enum AppTheme
    {
        Dark,
        Light
    }

    private readonly record struct ThemeColor(string Dark, string Light);

    private static readonly IReadOnlyDictionary<string, ThemeColor> ThemeBrushes = new Dictionary<string, ThemeColor>
    {
        ["WindowBackBrush"] = new("#181B1F", "#F2F4F7"),
        ["PanelBackBrush"] = new("#20252A", "#FFFFFF"),
        ["PanelAltBackBrush"] = new("#252B31", "#E9EEF3"),
        ["ControlBackBrush"] = new("#2A3037", "#FFFFFF"),
        ["ControlHoverBackBrush"] = new("#343C44", "#F4F8FC"),
        ["ControlPressedBackBrush"] = new("#1F6F98", "#CFE8F7"),
        ["InputBackBrush"] = new("#15181B", "#FFFFFF"),
        ["LogBackBrush"] = new("#111417", "#FFFFFF"),
        ["LineNumberBackBrush"] = new("#15191D", "#F0F3F6"),
        ["SelectionBackBrush"] = new("#24506A", "#B9DDF1"),
        ["SelectionTextBrush"] = new("#FFFFFF", "#0C1A24"),
        ["ScrollTrackBrush"] = new("#14181B", "#E6EBF0"),
        ["ScrollThumbBrush"] = new("#3C4A55", "#AEBCC8"),
        ["ScrollThumbHoverBrush"] = new("#4E5E6B", "#93A4B3"),
        ["ScrollThumbBorderBrush"] = new("#4A5662", "#9AAAB8"),
        ["BorderBrush"] = new("#3A424B", "#B9C3CD"),
        ["StrongBorderBrush"] = new("#56616B", "#8B98A5"),
        ["TextBrush"] = new("#E7EAED", "#20262D"),
        ["MutedTextBrush"] = new("#9DA7B0", "#5D6873"),
        ["AccentBrush"] = new("#4BA3D3", "#1B6F9D"),
        ["WarningBrush"] = new("#D7A747", "#A46609"),
        ["ErrorBrush"] = new("#D75F5F", "#B42318")
    };

    private readonly RangeObservableCollection<LogLine> _logLines = new();
    private readonly RangeObservableCollection<LogSearchResult> _searchResults = new();

    private LogFileDocument? _document;
    private CancellationTokenSource? _searchCts;
    private int _pageLineCount = MinPageLineCount;
    private long? _highlightedLineNumber;
    private long _currentOffset;
    private long _currentLineNumber = 1;
    private long _nextOffset;
    private bool _isChangingEncoding;
    private bool _isSearching;
    private bool _isOpening;
    private bool _isDraggingLogScrollThumb;
    private double _logScrollDragOffsetY;
    private int _searchPageLineCount = MinPageLineCount;
    private int _searchTopIndex;
    private bool _isDraggingSearchScrollThumb;
    private double _searchScrollDragOffsetY;
    private AppTheme _currentTheme = AppTheme.Dark;
    private bool _isUpdatingThemeToggle;
    private readonly AppSettings _settings = AppSettings.Load();
    private FontFamily _editorFontFamily = new(DefaultEditorFontFamily);
    private double _editorFontSize = DefaultEditorFontSize;
    private bool _isUpdatingFontSettings;
    private bool _isSettingsInitialized;
    private readonly DispatcherTimer _memoryStatusTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public MainWindow()
    {
        InitializeComponent();

        EncodingComboBox.SelectedIndex = 0;
        SourceInitialized += (_, _) => ApplyNativeTitleBarTheme();
        Closed += MainWindow_Closed;

        _editorFontFamily = new FontFamily(_settings.FontFamily);
        _editorFontSize = _settings.FontSize;
        ApplyEditorFont();

        ApplyTheme(_settings.IsDarkTheme ? AppTheme.Dark : AppTheme.Light);
        ResetStatus();
        UpdateControlState();

        _memoryStatusTimer.Tick += (_, _) => UpdateMemoryStatus();
        _memoryStatusTimer.Start();
        UpdateMemoryStatus();
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Multiselect = false,
            Filter = "Log files (*.log;*.txt)|*.log;*.txt|All files (*.*)|*.*",
            Title = "打开日志文件"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await OpenFileAsync(dialog.FileName, encodingOverride: null, startLineNumber: 1);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        await OpenFileAsync(_document.FilePath, GetSelectedEncoding(), _currentLineNumber);
    }

    private async void EncodingComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isChangingEncoding || _document is null)
        {
            return;
        }

        await OpenFileAsync(_document.FilePath, GetSelectedEncoding(), _currentLineNumber);
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        await StartSearchAsync();
    }

    private async void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await StartSearchAsync();
        }
    }

    private void CancelSearchButton_Click(object sender, RoutedEventArgs e)
    {
        _searchCts?.Cancel();
    }

    private void ThemeToggleButton_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isUpdatingThemeToggle)
        {
            ApplyTheme(AppTheme.Light);
            SaveSettings();
        }
    }

    private void ThemeToggleButton_Unchecked(object sender, RoutedEventArgs e)
    {
        if (!_isUpdatingThemeToggle)
        {
            ApplyTheme(AppTheme.Dark);
            SaveSettings();
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        EnsureSettingsInitialized();
        SettingsPopup.IsOpen = true;
    }

    private void SettingsPopup_Opened(object? sender, EventArgs e)
    {
        SyncSettingsControls();
    }

    private void EnsureSettingsInitialized()
    {
        if (_isSettingsInitialized)
        {
            return;
        }

        var families = Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        FontFamilyComboBox.ItemsSource = families;

        var sizes = new List<string>
        {
            "9", "10", "11", "12", "13", "14", "16", "18", "20", "24", "28", "32"
        };
        var currentSize = _editorFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!sizes.Contains(currentSize))
        {
            sizes.Add(currentSize);
            sizes = sizes
                .OrderBy(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture))
                .ToList();
        }

        FontSizeComboBox.ItemsSource = sizes;

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionTextBlock.Text = version is null
            ? "版本 1.0.0"
            : $"版本 {version.Major}.{version.Minor}.{version.Build}";

        _isSettingsInitialized = true;
    }

    private void SyncSettingsControls()
    {
        _isUpdatingFontSettings = true;

        FontFamilyComboBox.SelectedItem = FontFamilyComboBox.Items
            .OfType<string>()
            .FirstOrDefault(name => string.Equals(name, _editorFontFamily.Source, StringComparison.OrdinalIgnoreCase));

        var sizeText = _editorFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        FontSizeComboBox.SelectedItem = FontSizeComboBox.Items
            .OfType<string>()
            .FirstOrDefault(text => text == sizeText);

        _isUpdatingFontSettings = false;

        AssociateLogCheckBox.IsChecked = _settings.FileAssociations.Contains(".log");
        AssociateTxtCheckBox.IsChecked = _settings.FileAssociations.Contains(".txt");
    }

    private void FontFamilyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingFontSettings || FontFamilyComboBox.SelectedItem is not string family)
        {
            return;
        }

        _editorFontFamily = new FontFamily(family);
        ApplyEditorFont();
        SaveSettings();
    }

    private void FontSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingFontSettings)
        {
            return;
        }

        if (FontSizeComboBox.SelectedItem is string text &&
            double.TryParse(
                text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var size))
        {
            ApplyFontSize(size);
        }
    }

    private void ApplyFontSize(double size)
    {
        size = Math.Clamp(size, 6, 72);
        if (Math.Abs(size - _editorFontSize) < 0.01)
        {
            return;
        }

        _editorFontSize = size;
        ApplyEditorFont();
        SaveSettings();
    }

    private void SaveSettings()
    {
        _settings.FontFamily = _editorFontFamily.Source;
        _settings.FontSize = _editorFontSize;
        _settings.IsDarkTheme = _currentTheme == AppTheme.Dark;
        _settings.Save();
    }

    private void ApplyEditorFont()
    {
        LogContentBox.FontFamily = _editorFontFamily;
        LogContentBox.FontSize = _editorFontSize;
        LogLineNumberBox.FontFamily = _editorFontFamily;
        LogLineNumberBox.FontSize = _editorFontSize;
        SearchContentBox.FontFamily = _editorFontFamily;
        SearchContentBox.FontSize = _editorFontSize;
        SearchLineNumberBox.FontFamily = _editorFontFamily;
        SearchLineNumberBox.FontSize = _editorFontSize;

        UpdateLineNumberColumnWidth();

        if (LogContentBox.ActualHeight > 0)
        {
            UpdatePageLineCount(LogContentBox.ActualHeight);
        }

        if (SearchContentBox.ActualHeight > 0)
        {
            UpdateSearchPageLineCount(SearchContentBox.ActualHeight);
        }

        UpdateLogHighlight();
    }

    private void SearchContentBox_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateSearchPageLineCount(e.NewSize.Height);
    }

    private void UpdateSearchPageLineCount(double viewportHeight)
    {
        var lineHeight = GetSearchContentLineHeight();
        var usableHeight = viewportHeight - SearchContentBox.Padding.Top - SearchContentBox.Padding.Bottom;
        var newCount = Math.Max(MinPageLineCount, (int)(usableHeight / lineHeight));
        if (newCount == _searchPageLineCount)
        {
            return;
        }

        _searchPageLineCount = newCount;
        UpdateSearchScrollAvailability();
        ClampSearchTopIndex();
        RefreshSearchTextBoxes();
        UpdateSearchScrollThumb();
    }

    private double GetSearchContentLineHeight()
    {
        var lineHeight = SearchContentBox.FontFamily.LineSpacing * SearchContentBox.FontSize;
        return lineHeight > 0 ? lineHeight : LogLineHeight;
    }

    private void SearchContentBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (SearchContentBox.SelectionLength > 0)
        {
            return;
        }

        var lineInView = SearchContentBox.GetLineIndexFromCharacterIndex(SearchContentBox.CaretIndex);
        if (lineInView < 0)
        {
            return;
        }

        var resultIndex = _searchTopIndex + lineInView;
        if (resultIndex < 0 || resultIndex >= _searchResults.Count)
        {
            return;
        }

        SelectBoxLine(SearchContentBox, lineInView);
        NavigateToSearchResult(_searchResults[resultIndex]);
    }

    private void NavigateToSearchResult(LogSearchResult result)
    {
        var startLine = result.LineNumber - _pageLineCount / 2;
        if (_document is not null)
        {
            var maxStartLine = Math.Max(1, _document.LineCount - _pageLineCount + 1);
            startLine = Math.Clamp(startLine, 1, maxStartLine);
        }
        else if (startLine < 1)
        {
            startLine = 1;
        }

        _highlightedLineNumber = result.LineNumber;
        LoadPageByLineNumber(startLine, updateScrollBar: true);
    }

    private void ClearLogHighlight()
    {
        _highlightedLineNumber = null;
        LogHighlightBar.Visibility = Visibility.Collapsed;
    }

    private void MainLog_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ClearLogHighlight();
    }

    private void UpdateLogHighlight()
    {
        LogHighlightBar.Visibility = Visibility.Collapsed;

        if (_highlightedLineNumber is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ApplyLogHighlight));
    }

    private void ApplyLogHighlight()
    {
        LogHighlightBar.Visibility = Visibility.Collapsed;

        if (_highlightedLineNumber is not long lineNumber)
        {
            return;
        }

        var found = false;
        var start = 0;
        for (var i = 0; i < _logLines.Count; i++)
        {
            if (_logLines[i].LineNumber == lineNumber)
            {
                found = true;
                break;
            }

            start += _logLines[i].Text.Length + 1;
        }

        if (!found)
        {
            return;
        }

        var rect = LogContentBox.GetRectFromCharacterIndex(start);
        if (rect.IsEmpty || double.IsInfinity(rect.Top) || double.IsNaN(rect.Top))
        {
            return;
        }

        var height = rect.Height > 0 ? rect.Height : GetLogContentLineHeight();
        LogHighlightBar.Margin = new Thickness(0, rect.Top, 0, 0);
        LogHighlightBar.Height = height;
        LogHighlightBar.Visibility = Visibility.Visible;
    }

    private static void SelectBoxLine(TextBox box, int lineIndex)
    {
        if (box.LineCount <= 0 || lineIndex < 0 || lineIndex >= box.LineCount)
        {
            return;
        }

        var start = box.GetCharacterIndexFromLineIndex(lineIndex);
        if (start < 0)
        {
            return;
        }

        var length = box.GetLineLength(lineIndex);
        var text = box.Text;
        while (length > 0 &&
               start + length - 1 < text.Length &&
               (text[start + length - 1] == '\n' || text[start + length - 1] == '\r'))
        {
            length--;
        }

        box.Select(start, length);
    }

    private void LogScrollTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanScrollLog())
        {
            return;
        }

        var pointerY = e.GetPosition(LogScrollTrack).Y;
        var thumbTop = LogScrollThumbTransform.Y;
        var thumbHeight = GetLogScrollThumbHeight();
        _logScrollDragOffsetY = pointerY >= thumbTop && pointerY <= thumbTop + thumbHeight
            ? pointerY - thumbTop
            : thumbHeight / 2;
        _isDraggingLogScrollThumb = true;
        LogScrollTrack.CaptureMouse();
        SetLogScrollFromThumbTop(pointerY - _logScrollDragOffsetY);
        e.Handled = true;
    }

    private void LogScrollTrack_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingLogScrollThumb)
        {
            return;
        }

        var pointerY = e.GetPosition(LogScrollTrack).Y;
        SetLogScrollFromThumbTop(pointerY - _logScrollDragOffsetY);
        e.Handled = true;
    }

    private void LogScrollTrack_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingLogScrollThumb)
        {
            return;
        }

        _isDraggingLogScrollThumb = false;
        LogScrollTrack.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void LogScrollTrack_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _isDraggingLogScrollThumb = false;
    }

    private void LogScrollTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLogScrollThumb();
    }

    private void LogContentBox_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePageLineCount(e.NewSize.Height);
    }

    private void UpdatePageLineCount(double viewportHeight)
    {
        var lineHeight = GetLogContentLineHeight();
        var usableHeight = viewportHeight - LogContentBox.Padding.Top - LogContentBox.Padding.Bottom;
        var newCount = Math.Max(MinPageLineCount, (int)(usableHeight / lineHeight));
        if (newCount == _pageLineCount)
        {
            return;
        }

        _pageLineCount = newCount;
        UpdateLogScrollAvailability();

        if (_document is not null && !_isOpening)
        {
            ReloadCurrentPage();
        }
        else
        {
            UpdateLogScrollThumb();
        }
    }

    private double GetLogContentLineHeight()
    {
        var lineHeight = LogContentBox.FontFamily.LineSpacing * LogContentBox.FontSize;
        return lineHeight > 0 ? lineHeight : LogLineHeight;
    }

    private void ReloadCurrentPage()
    {
        if (_document is null)
        {
            return;
        }

        var maxStartLine = Math.Max(1, _document.LineCount - _pageLineCount + 1);
        var targetLineNumber = Math.Clamp(_currentLineNumber, 1, maxStartLine);
        LoadPageByLineNumber(targetLineNumber, updateScrollBar: true);
    }

    private void MainLogGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!CanScrollLog())
        {
            return;
        }

        if (e.Delta == 0)
        {
            return;
        }

        var lineDelta = (long)Math.Round(-e.Delta / 120.0 * LogWheelLinesPerDetent);
        if (lineDelta == 0)
        {
            lineDelta = e.Delta > 0 ? -1 : 1;
        }

        ScrollLogByLines(lineDelta);
        e.Handled = true;
    }

    private void MainWindow_Closed(object? sender, EventArgs args)
    {
        _memoryStatusTimer.Stop();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _document?.Dispose();
    }

    private void ApplyTheme(AppTheme theme)
    {
        _currentTheme = theme;

        foreach (var (brushKey, colors) in ThemeBrushes)
        {
            SetBrushColor(brushKey, theme == AppTheme.Dark ? colors.Dark : colors.Light);
        }

        ApplyInactiveSelectionHighlight(theme);

        _isUpdatingThemeToggle = true;
        ThemeToggleButton.IsChecked = theme == AppTheme.Light;
        ThemeToggleButton.Content = theme == AppTheme.Dark ? "深色" : "浅色";
        ThemeToggleButton.ToolTip = theme == AppTheme.Dark
            ? "当前深色模式，点击切换浅色模式"
            : "当前浅色模式，点击切换深色模式";
        _isUpdatingThemeToggle = false;

        ApplyNativeTitleBarTheme();
    }

    private void ApplyNativeTitleBarTheme()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var useDarkMode = _currentTheme == AppTheme.Dark ? 1 : 0;
        try
        {
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref useDarkMode, Marshal.SizeOf<int>());
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    private async Task OpenFileAsync(string filePath, LogTextEncoding? encodingOverride, long startLineNumber)
    {
        CancelCurrentSearch();
        _isOpening = true;
        SearchStatusTextBlock.Text = "加载中";
        SearchProgressBar.Visibility = Visibility.Collapsed;
        UpdateControlState();

        try
        {
            var oldDocument = _document;
            _document = null;
            oldDocument?.Dispose();
            oldDocument = null;
            _highlightedLineNumber = null;
            _logLines.Clear();
            _searchResults.Clear();
            RefreshLogTextBoxes();
            ClearSearchTextBoxes();
            ConfigureScrollBar();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            var stopwatch = Stopwatch.StartNew();
            var newDocument = await Task.Run(() => LogFileDocument.Open(filePath, encodingOverride));
            stopwatch.Stop();
            _document = newDocument;

            SelectEncoding(newDocument.EncodingKind);
            UpdateLineNumberColumnWidth();
            SearchResultStatusTextBlock.Text = "搜索结果：0 条";
            SearchStatusTextBlock.Text = $"加载完成：{stopwatch.Elapsed.TotalSeconds:0.00}s";

            ConfigureScrollBar();
            LoadPageByLineNumber(Math.Max(1, startLineNumber), updateScrollBar: true);
        }
        catch (Exception ex)
        {
            SearchStatusTextBlock.Text = "打开失败";
            await ShowErrorAsync("打开失败", DescribeException(ex));
            UpdateDocumentStatus();
        }
        finally
        {
            _isOpening = false;
            UpdateControlState();
        }
    }

    private void LoadPageByLineNumber(long startLineNumber, bool updateScrollBar)
    {
        if (_document is null)
        {
            return;
        }

        try
        {
            var page = _document.ReadLinesFromLine(startLineNumber, _pageLineCount);

            _logLines.Clear();
            _logLines.AddRange(page.Lines);

            _currentOffset = page.StartOffset;
            _currentLineNumber = page.Lines.Count > 0 ? page.Lines[0].LineNumber : 1;
            _nextOffset = page.NextOffset;

            RefreshLogTextBoxes();
            if (updateScrollBar)
            {
                SetScrollBarValue(_currentLineNumber);
            }

            UpdateDocumentStatus();
        }
        catch (Exception ex)
        {
            _ = ShowErrorAsync("读取失败", DescribeException(ex));
        }
    }

    private async Task StartSearchAsync()
    {
        var document = _document;
        if (document is null || _isSearching || _isOpening)
        {
            return;
        }

        var pattern = SearchTextBox.Text;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            await ShowErrorAsync("无法搜索", "请输入搜索条件。");
            return;
        }

        var searchCts = new CancellationTokenSource();
        _searchCts = searchCts;
        _isSearching = true;
        _searchResults.Clear();
        ClearSearchTextBoxes();
        SearchProgressBar.Value = 0;
        SearchProgressBar.Visibility = Visibility.Visible;
        SearchResultStatusTextBlock.Text = "搜索结果：0 条";
        SearchStatusTextBlock.Text = "搜索中";
        UpdateControlState();

        var progress = new Progress<LogSearchProgress>(UpdateSearchProgress);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var summary = await document.SearchAsync(
                pattern,
                RegexButton.IsChecked == true,
                CaseSensitiveButton.IsChecked == true,
                AddSearchResultBatch,
                progress,
                searchCts.Token);

            stopwatch.Stop();
            SearchProgressBar.Value = 100;
            SearchStatusTextBlock.Text = $"完成：{summary.MatchCount} 条  {stopwatch.Elapsed.TotalSeconds:0.00}s";
            SearchResultStatusTextBlock.Text = $"搜索结果：{summary.MatchCount} 条";
        }
        catch (OperationCanceledException)
        {
            SearchStatusTextBlock.Text = $"已取消：{_searchResults.Count} 条";
            SearchResultStatusTextBlock.Text = $"搜索结果：{_searchResults.Count} 条";
        }
        catch (ArgumentException ex)
        {
            SearchStatusTextBlock.Text = "搜索失败";
            await ShowErrorAsync("搜索失败", DescribeException(ex));
        }
        catch (Exception ex)
        {
            SearchStatusTextBlock.Text = "搜索失败";
            await ShowErrorAsync("搜索失败", DescribeException(ex));
        }
        finally
        {
            if (ReferenceEquals(_searchCts, searchCts))
            {
                _searchCts = null;
            }

            searchCts.Dispose();
            _isSearching = false;
            SearchProgressBar.Visibility = Visibility.Collapsed;
            UpdateControlState();
        }
    }

    private void AddSearchResultBatch(IReadOnlyList<LogSearchResult> batch)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            _searchResults.AddRange(batch);

            RefreshSearchTextBoxes();
            UpdateSearchScrollAvailability();
            UpdateSearchScrollThumb();
            SearchResultStatusTextBlock.Text = $"搜索结果：{_searchResults.Count} 条";
        }, DispatcherPriority.Background);
    }

    private void RefreshLogTextBoxes()
    {
        var numbers = new StringBuilder();
        var content = new StringBuilder();
        for (var i = 0; i < _logLines.Count; i++)
        {
            if (i > 0)
            {
                numbers.Append('\n');
                content.Append('\n');
            }

            numbers.Append(_logLines[i].LineNumber);
            content.Append(_logLines[i].Text);
        }

        LogLineNumberBox.Text = numbers.ToString();
        LogContentBox.Text = content.ToString();
        UpdateLogHighlight();
    }

    private void RefreshSearchTextBoxes()
    {
        if (_searchResults.Count == 0)
        {
            if (SearchContentBox.Text.Length > 0)
            {
                SearchLineNumberBox.Clear();
                SearchContentBox.Clear();
            }

            return;
        }

        ClampSearchTopIndex();

        var count = Math.Min(_searchPageLineCount, _searchResults.Count - _searchTopIndex);
        var numbers = new StringBuilder();
        var content = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                numbers.Append('\n');
                content.Append('\n');
            }

            var result = _searchResults[_searchTopIndex + i];
            numbers.Append(result.LineNumber);
            content.Append(result.Text);
        }

        SearchLineNumberBox.Text = numbers.ToString();
        SearchContentBox.Text = content.ToString();
    }

    private void ClampSearchTopIndex()
    {
        var maxTopIndex = Math.Max(0, _searchResults.Count - _searchPageLineCount);
        _searchTopIndex = (int)Math.Clamp((long)_searchTopIndex, 0, maxTopIndex);
    }

    private void UpdateLineNumberColumnWidth()
    {
        if (_document is null)
        {
            return;
        }

        var digits = Math.Max(1L, _document.LineCount)
            .ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
        var sample = new string('9', digits);
        var dpi = VisualTreeHelper.GetDpi(this);
        var typeface = new Typeface(
            _editorFontFamily,
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);
        var formatted = new FormattedText(
            sample,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            _editorFontSize,
            Brushes.Black,
            dpi.PixelsPerDip);

        var width = new GridLength(Math.Ceiling(formatted.WidthIncludingTrailingWhitespace + 11));
        LogLineNumberColumn.Width = width;
        SearchLineNumberColumn.Width = width;
    }

    private void ClearSearchTextBoxes()
    {
        _searchTopIndex = 0;
        SearchLineNumberBox.Clear();
        SearchContentBox.Clear();
        UpdateSearchScrollAvailability();
        UpdateSearchScrollThumb();
    }

    private void UpdateSearchProgress(LogSearchProgress progress)
    {
        var percent = progress.TotalBytes == 0
            ? 100
            : Math.Clamp(progress.BytesRead * 100.0 / progress.TotalBytes, 0, 100);

        SearchProgressBar.Value = percent;
        SearchStatusTextBlock.Text = $"搜索中：{progress.MatchCount} 条";
    }

    private void CancelCurrentSearch()
    {
        _searchCts?.Cancel();
    }

    private void ConfigureScrollBar()
    {
        UpdateLogScrollAvailability();
        UpdateLogScrollThumb();
        UpdateSearchScrollAvailability();
        UpdateSearchScrollThumb();
    }

    private void SetScrollBarValue(long lineNumber)
    {
        if (_document is null)
        {
            return;
        }

        UpdateLogScrollThumb();
    }

    private void SelectEncoding(LogTextEncoding encoding)
    {
        _isChangingEncoding = true;
        EncodingComboBox.SelectedIndex = encoding == LogTextEncoding.Utf8 ? 0 : 1;
        _isChangingEncoding = false;
    }

    private LogTextEncoding GetSelectedEncoding()
    {
        return EncodingComboBox.SelectedIndex == 1 ? LogTextEncoding.Gbk : LogTextEncoding.Utf8;
    }

    private void UpdateMemoryStatus()
    {
        var available = LogFileDocument.GetAvailablePhysicalMemory();
        MemoryStatusTextBlock.Text = $"剩余内存：{FormatBytes(available)} · 可打开上限：{FormatBytes(LogFileDocument.MaxFileSize)}";
    }

    private void ResetStatus()
    {
        FilePathTextBlock.Text = "未打开文件";
        FileSizeTextBlock.Text = string.Empty;
        EncodingStatusTextBlock.Text = string.Empty;
        SearchStatusTextBlock.Text = "就绪";
        SearchResultStatusTextBlock.Text = "搜索结果：0 条";
        SearchProgressBar.Visibility = Visibility.Collapsed;
        ConfigureScrollBar();
    }

    private void UpdateDocumentStatus()
    {
        if (_document is null)
        {
            ResetStatus();
            return;
        }

        FilePathTextBlock.Text = _document.FilePath;
        FileSizeTextBlock.Text = $"{FormatBytes(_document.FileSize)}  {_document.LineCount:n0} 行  当前 {_currentLineNumber:n0}";
        EncodingStatusTextBlock.Text = FormatEncoding(_document.EncodingKind);
    }

    private void UpdateControlState()
    {
        var hasDocument = _document is not null;
        var canChangeDocument = !_isOpening && !_isSearching;

        OpenButton.IsEnabled = canChangeDocument;
        RefreshButton.IsEnabled = hasDocument && canChangeDocument;
        EncodingComboBox.IsEnabled = hasDocument && canChangeDocument;
        SearchButton.IsEnabled = hasDocument && !_isSearching && !_isOpening;
        CancelSearchButton.IsEnabled = _isSearching;
        UpdateLogScrollAvailability();
        UpdateLogScrollThumb();
    }

    private bool CanScrollLog()
    {
        return _document is not null && !_isOpening && _document.LineCount > _pageLineCount;
    }

    private void UpdateLogScrollAvailability()
    {
        var canScroll = CanScrollLog();
        LogScrollTrack.IsHitTestVisible = canScroll;
        LogScrollTrack.Opacity = canScroll ? 1 : 0.35;
        LogScrollThumb.Visibility = canScroll ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateLogScrollThumb()
    {
        if (!CanScrollLog() || LogScrollTrack.ActualHeight <= 0)
        {
            LogScrollThumbTransform.Y = 0;
            return;
        }

        var trackHeight = LogScrollTrack.ActualHeight;
        var thumbHeight = Math.Clamp(
            trackHeight * _pageLineCount / _document!.LineCount,
            LogScrollThumb.MinHeight,
            trackHeight);
        var maxThumbTop = Math.Max(0, trackHeight - thumbHeight);
        var maxValue = Math.Max(1, _document.LineCount - _pageLineCount);
        var value = Math.Clamp(_currentLineNumber - 1, 0, maxValue);

        LogScrollThumb.Height = thumbHeight;
        LogScrollThumbTransform.Y = maxThumbTop * value / maxValue;
    }

    private void SetLogScrollFromThumbTop(double requestedTop)
    {
        if (!CanScrollLog() || LogScrollTrack.ActualHeight <= 0)
        {
            return;
        }

        var thumbHeight = GetLogScrollThumbHeight();
        var maxThumbTop = Math.Max(0, LogScrollTrack.ActualHeight - thumbHeight);
        var thumbTop = Math.Clamp(requestedTop, 0, maxThumbTop);
        var maxValue = Math.Max(1, _document!.LineCount - _pageLineCount);
        var lineOffset = maxThumbTop <= 0
            ? 0
            : (long)Math.Round(thumbTop / maxThumbTop * maxValue);

        LoadPageByLineNumber(lineOffset + 1, updateScrollBar: true);
    }

    private void ScrollLogByLines(long lineDelta)
    {
        if (_document is null || lineDelta == 0)
        {
            return;
        }

        var maxStartLine = Math.Max(1, _document.LineCount - _pageLineCount + 1);
        var targetLineNumber = Math.Clamp(_currentLineNumber + lineDelta, 1, maxStartLine);
        if (targetLineNumber == _currentLineNumber)
        {
            return;
        }

        LoadPageByLineNumber(targetLineNumber, updateScrollBar: true);
    }

    private double GetLogScrollThumbHeight()
    {
        return LogScrollThumb.ActualHeight > 0 ? LogScrollThumb.ActualHeight : LogScrollThumb.Height;
    }

    private void SearchGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!CanScrollSearch() || e.Delta == 0)
        {
            return;
        }

        var lineDelta = (long)Math.Round(-e.Delta / 120.0 * LogWheelLinesPerDetent);
        if (lineDelta == 0)
        {
            lineDelta = e.Delta > 0 ? -1 : 1;
        }

        ScrollSearchByLines(lineDelta);
        e.Handled = true;
    }

    private void ScrollSearchByLines(long lineDelta)
    {
        if (lineDelta == 0)
        {
            return;
        }

        var maxTopIndex = Math.Max(0, _searchResults.Count - _searchPageLineCount);
        var target = (int)Math.Clamp((long)_searchTopIndex + lineDelta, 0, maxTopIndex);
        if (target == _searchTopIndex)
        {
            return;
        }

        _searchTopIndex = target;
        RefreshSearchTextBoxes();
        UpdateSearchScrollThumb();
    }

    private void SearchScrollTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanScrollSearch())
        {
            return;
        }

        var pointerY = e.GetPosition(SearchScrollTrack).Y;
        var thumbTop = SearchScrollThumbTransform.Y;
        var thumbHeight = GetSearchScrollThumbHeight();
        _searchScrollDragOffsetY = pointerY >= thumbTop && pointerY <= thumbTop + thumbHeight
            ? pointerY - thumbTop
            : thumbHeight / 2;
        _isDraggingSearchScrollThumb = true;
        SearchScrollTrack.CaptureMouse();
        SetSearchScrollFromThumbTop(pointerY - _searchScrollDragOffsetY);
        e.Handled = true;
    }

    private void SearchScrollTrack_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingSearchScrollThumb)
        {
            return;
        }

        var pointerY = e.GetPosition(SearchScrollTrack).Y;
        SetSearchScrollFromThumbTop(pointerY - _searchScrollDragOffsetY);
        e.Handled = true;
    }

    private void SearchScrollTrack_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingSearchScrollThumb)
        {
            return;
        }

        _isDraggingSearchScrollThumb = false;
        SearchScrollTrack.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void SearchScrollTrack_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _isDraggingSearchScrollThumb = false;
    }

    private void SearchScrollTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateSearchScrollThumb();
    }

    private void SetSearchScrollFromThumbTop(double requestedTop)
    {
        if (!CanScrollSearch() || SearchScrollTrack.ActualHeight <= 0)
        {
            return;
        }

        var thumbHeight = GetSearchScrollThumbHeight();
        var maxThumbTop = Math.Max(0, SearchScrollTrack.ActualHeight - thumbHeight);
        var thumbTop = Math.Clamp(requestedTop, 0, maxThumbTop);
        var maxValue = Math.Max(1, _searchResults.Count - _searchPageLineCount);
        var target = maxThumbTop <= 0
            ? 0
            : (int)Math.Clamp(Math.Round(thumbTop / maxThumbTop * maxValue), 0, maxValue);

        if (target == _searchTopIndex)
        {
            UpdateSearchScrollThumb();
            return;
        }

        _searchTopIndex = target;
        RefreshSearchTextBoxes();
        UpdateSearchScrollThumb();
    }

    private bool CanScrollSearch()
    {
        return _searchResults.Count > _searchPageLineCount;
    }

    private void UpdateSearchScrollAvailability()
    {
        var canScroll = CanScrollSearch();
        SearchScrollTrack.IsHitTestVisible = canScroll;
        SearchScrollTrack.Opacity = canScroll ? 1 : 0.35;
        SearchScrollThumb.Visibility = canScroll ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSearchScrollThumb()
    {
        if (!CanScrollSearch() || SearchScrollTrack.ActualHeight <= 0)
        {
            SearchScrollThumbTransform.Y = 0;
            return;
        }

        var trackHeight = SearchScrollTrack.ActualHeight;
        var thumbHeight = Math.Clamp(
            trackHeight * _searchPageLineCount / _searchResults.Count,
            SearchScrollThumb.MinHeight,
            trackHeight);
        var maxThumbTop = Math.Max(0, trackHeight - thumbHeight);
        var maxValue = Math.Max(1, _searchResults.Count - _searchPageLineCount);
        var value = Math.Clamp((long)_searchTopIndex, 0, maxValue);

        SearchScrollThumb.Height = thumbHeight;
        SearchScrollThumbTransform.Y = maxThumbTop * value / maxValue;
    }

    private double GetSearchScrollThumbHeight()
    {
        return SearchScrollThumb.ActualHeight > 0 ? SearchScrollThumb.ActualHeight : SearchScrollThumb.Height;
    }

    private static void ApplyInactiveSelectionHighlight(AppTheme theme)
    {
        var colorText = theme == AppTheme.Dark
            ? ThemeBrushes["SelectionBackBrush"].Dark
            : ThemeBrushes["SelectionBackBrush"].Light;

        if (ColorConverter.ConvertFromString(colorText) is not Color color)
        {
            return;
        }

        Application.Current.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = new SolidColorBrush(color);
    }

    private static void SetBrushColor(string brushKey, string colorText)
    {
        if (ColorConverter.ConvertFromString(colorText) is not Color color)
        {
            return;
        }

        Application.Current.Resources[brushKey] = new SolidColorBrush(color);
    }

    private Task ShowErrorAsync(string title, string message)
    {
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        return Task.CompletedTask;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    private static string FormatEncoding(LogTextEncoding encoding)
    {
        return encoding == LogTextEncoding.Utf8 ? "UTF-8" : "GBK";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var value = (double)bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private void FileAssociationCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        var associations = new HashSet<string>(_settings.FileAssociations);

        void Update(string ext, bool? isChecked)
        {
            if (isChecked == true)
            {
                associations.Add(ext);
            }
            else
            {
                associations.Remove(ext);
            }
        }

        Update(".log", AssociateLogCheckBox.IsChecked);
        Update(".txt", AssociateTxtCheckBox.IsChecked);

        _settings.FileAssociations = associations.OrderBy(x => x).ToList();
        SaveSettings();
    }

    private void ApplyFileAssociationButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                return;
            }

            var allExtensions = new[] { ".log", ".txt" };
            foreach (var ext in allExtensions)
            {
                var progId = $"LogRAM{ext}";
                var shouldAssociate = _settings.FileAssociations.Contains(ext);
                SetFileAssociation(ext, progId, exePath, shouldAssociate);
            }

            var boundList = string.Join("、", _settings.FileAssociations.DefaultIfEmpty("无"));
            MessageBox.Show(
                this,
                $"文件类型绑定已更新。\n\n当前默认绑定：{boundList}\n双击这些类型的文件将自动使用 LogRAM 打开。",
                "绑定完成",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _ = ShowErrorAsync("绑定失败", DescribeException(ex));
        }
    }

    private static void SetFileAssociation(string extension, string progId, string exePath, bool enable)
    {
        using var classesKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true);
        if (classesKey is null)
        {
            return;
        }

        if (enable)
        {
            using var progKey = classesKey.CreateSubKey(progId);
            progKey.SetValue("FriendlyTypeName", $"LogRAM {extension} 日志文件", RegistryValueKind.String);
            using var iconKey = progKey.CreateSubKey("DefaultIcon");
            iconKey.SetValue(null, $"\"{exePath}\",0", RegistryValueKind.String);
            using var shellKey = progKey.CreateSubKey(@"shell\open\command");
            shellKey.SetValue(null, $"\"{exePath}\" \"%1\"", RegistryValueKind.String);

            using var extKey = classesKey.CreateSubKey(extension);
            extKey.SetValue(null, progId, RegistryValueKind.String);
            using var progIdsKey = extKey.CreateSubKey("OpenWithProgids");
            progIdsKey.SetValue(progId, string.Empty, RegistryValueKind.String);

            using var appKey = Registry.CurrentUser.CreateSubKey(@"Software\LogRAM\Capabilities\FileAssociations");
            appKey.SetValue(extension, progId, RegistryValueKind.String);

            using var regApps = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications");
            regApps.SetValue("LogRAM", @"Software\LogRAM\Capabilities", RegistryValueKind.String);

            SetUserChoiceHash(extension, progId);
        }
        else
        {
            try
            {
                using var extKey = classesKey.OpenSubKey(extension, writable: true);
                if (extKey is not null)
                {
                    if (extKey.GetValue(string.Empty) as string == progId)
                    {
                        extKey.DeleteValue(string.Empty, throwOnMissingValue: false);
                    }

                    using var progIdsKey = extKey.OpenSubKey("OpenWithProgids", writable: true);
                    progIdsKey?.DeleteValue(progId, throwOnMissingValue: false);
                }
            }
            catch
            {
            }

            try
            {
                classesKey.DeleteSubKeyTree(progId, throwOnMissingSubKey: false);
            }
            catch
            {
            }

            try
            {
                using var appKey = Registry.CurrentUser.OpenSubKey(@"Software\LogRAM\Capabilities\FileAssociations", writable: true);
                appKey?.DeleteValue(extension, throwOnMissingValue: false);
            }
            catch
            {
            }

            try
            {
                using var userChoiceKey = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}", writable: true);
                userChoiceKey?.DeleteSubKey("UserChoice", throwOnMissingSubKey: false);
            }
            catch
            {
            }
        }
    }

    private static void SetUserChoiceHash(string extension, string progId)
    {
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (string.IsNullOrEmpty(sid))
            {
                return;
            }

            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            var experience = "User Choice set via Windows User Experience";
            var hashInput = $"{extension}{sid}{progId}{timestamp}{experience}";
            var hash = SHA256.HashData(Encoding.Unicode.GetBytes(hashInput));
            var hashBase64 = Convert.ToBase64String(hash);

            using var userChoiceKey = Registry.CurrentUser.CreateSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}\UserChoice");
            userChoiceKey.SetValue("ProgId", progId, RegistryValueKind.String);
            userChoiceKey.SetValue("Hash", hashBase64, RegistryValueKind.String);
        }
        catch
        {
        }
    }

    public async Task OpenFileFromArgsAsync(string filePath)
    {
        await OpenFileAsync(filePath, encodingOverride: null, startLineNumber: 1);
    }

    private static string DescribeException(Exception ex)
    {
        return ex switch
        {
            FileNotFoundException => "文件不存在。",
            UnauthorizedAccessException => "没有权限访问该文件。",
            InvalidOperationException when ex.Message.Contains("available memory limit", StringComparison.OrdinalIgnoreCase) => $"日志文件超过了可用内存上限（启动时剩余内存的 80%，约 {FormatBytes(LogFileDocument.MaxFileSize)}）。",
            OutOfMemoryException => "内存不足，无法构造日志索引。",
            ArgumentException when ex.Message.Contains("pattern", StringComparison.OrdinalIgnoreCase) => "搜索条件不能为空。",
            _ => ex.Message
        };
    }
}
