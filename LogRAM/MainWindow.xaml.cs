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
    private const double HorizontalScrollBarHeight = 14;
    private const int MinPageLineCount = 1;
    private const int LogWheelLinesPerDetent = 3;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const double DefaultEditorFontSize = 12;
    private const string DefaultEditorFontFamily = "Consolas";
    private const int MaxRecentFiles = 20;
    private const int MaxSearchHistory = 20;
    private static readonly TimeSpan LiveRefreshInterval = TimeSpan.FromMilliseconds(500);

    private enum AppTheme
    {
        Dark,
        Light
    }

    private readonly record struct ThemeColor(string Dark, string Light);

    private sealed record SearchCriteria(
        string Pattern,
        bool UseRegex,
        bool CaseSensitive,
        AdvancedSearchQuery? AdvancedQuery);

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
    private readonly HashSet<long> _searchResultLineNumbers = new();
    private readonly Dictionary<long, LogSearchResult> _searchResultsByLineNumber = new();

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
    private int _selectedSearchResultIndex = -1;
    private bool _isDraggingSearchScrollThumb;
    private double _searchScrollDragOffsetY;
    private double _logMaxContentWidth;
    private double _searchMaxContentWidth;
    private bool _logReserveHScroll;
    private bool _searchReserveHScroll;
    private bool _logHScrollStatePending;
    private bool _searchHScrollStatePending;
    private ScrollViewer? _logContentScrollViewer;
    private ScrollViewer? _searchContentScrollViewer;
    private AppTheme _currentTheme = AppTheme.Dark;
    private bool _isUpdatingThemeToggle;
    private readonly AppSettings _settings = AppSettings.Load();
    private FontFamily _editorFontFamily = new(DefaultEditorFontFamily);
    private double _editorFontSize = DefaultEditorFontSize;
    private bool _isUpdatingFontSettings;
    private bool _isSettingsInitialized;
    private readonly DispatcherTimer _memoryStatusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _liveRefreshTimer = new() { Interval = LiveRefreshInterval };
    private bool _isLiveRefreshEnabled;
    private bool _isRefreshingLive;
    private SearchCriteria? _activeSearchCriteria;

    public MainWindow()
    {
        InitializeComponent();

        EncodingComboBox.SelectedIndex = 0;
        SourceInitialized += (_, _) => ApplyNativeTitleBarTheme();
        Closed += MainWindow_Closed;

        Loc.SetLanguage(Loc.Parse(_settings.Language));

        _editorFontFamily = new FontFamily(_settings.FontFamily);
        _editorFontSize = _settings.FontSize;
        ApplyEditorFont();

        ApplyTheme(_settings.IsDarkTheme ? AppTheme.Dark : AppTheme.Light);
        ApplyLanguage();
        RefreshSearchHistoryItems();
        ResetStatus();
        UpdateControlState();

        _memoryStatusTimer.Tick += (_, _) => UpdateMemoryStatus();
        _memoryStatusTimer.Start();
        _liveRefreshTimer.Tick += LiveRefreshTimer_Tick;
        UpdateMemoryStatus();
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Multiselect = false,
            Filter = Loc.S.OpenDialogFilter,
            Title = Loc.S.OpenDialogTitle
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await OpenOrLaunchAsync(dialog.FileName);
    }

    private async Task OpenOrLaunchAsync(string filePath)
    {
        // 已打开文件（或正忙）时，再次打开则启动新的 LogRAM 实例，避免覆盖当前文件
        if (_document is not null || _isOpening || _isSearching)
        {
            LaunchNewInstance(filePath);
            return;
        }

        await OpenFileAsync(filePath, encodingOverride: null, startLineNumber: 1);
    }

    private void LaunchNewInstance(string filePath)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(filePath);
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            _ = ShowErrorAsync(Loc.S.OpenFailedTitle, DescribeException(ex));
        }
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return;
        }

        e.Handled = true;

        foreach (var file in files)
        {
            if (File.Exists(file))
            {
                await OpenOrLaunchAsync(file);
            }
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        await OpenFileAsync(_document.FilePath, GetSelectedEncoding(), _currentLineNumber);
    }

    private async void LiveRefreshButton_Checked(object sender, RoutedEventArgs e)
    {
        SetLiveRefreshEnabled(true);
        await RefreshLiveAsync();
    }

    private void LiveRefreshButton_Unchecked(object sender, RoutedEventArgs e)
    {
        SetLiveRefreshEnabled(false);
    }

    private async void LiveRefreshTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshLiveAsync();
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

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.F)
        {
            e.Handled = true;
            FocusSearchBox();
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.G)
        {
            e.Handled = true;
            ShowJumpLineDialog();
            return;
        }

        if (e.Key == Key.F3)
        {
            e.Handled = true;
            NavigateSearchResult((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ? -1 : 1);
        }
    }
    private void RecentButton_Click(object sender, RoutedEventArgs e)
    {
        var files = _settings.RecentFiles.Where(File.Exists).ToList();
        if (files.Count == 0)
        {
            return;
        }

        var menu = new ContextMenu();
        foreach (var file in files)
        {
            var item = new MenuItem
            {
                Header = Path.GetFileName(file),
                ToolTip = file
            };
            item.Click += async (_, _) => await OpenOrLaunchAsync(file);
            menu.Items.Add(item);
        }

        ShowButtonMenu(RecentButton, menu);
    }

    private void SearchHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.SearchHistory.Count == 0)
        {
            return;
        }

        var menu = new ContextMenu();
        foreach (var query in _settings.SearchHistory)
        {
            var item = new MenuItem { Header = query };
            item.Click += (_, _) =>
            {
                SearchTextBox.Text = query;
                SearchTextBox.Focus();
                SearchTextBox.CaretIndex = SearchTextBox.Text.Length;
            };
            menu.Items.Add(item);
        }

        ShowButtonMenu(SearchHistoryButton, menu);
    }

    private static void ShowButtonMenu(Button button, ContextMenu menu)
    {
        button.ContextMenu = menu;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void PreviousResultButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateSearchResult(-1);
    }

    private void NextResultButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateSearchResult(1);
    }

    private void FocusSearchBox()
    {
        SearchTextBox.Focus();
        SearchTextBox.SelectAll();
    }

    private void ShowJumpLineDialog()
    {
        if (_document is null)
        {
            return;
        }

        var input = new TextBox
        {
            Text = _currentLineNumber.ToString(),
            MinWidth = 180,
            Margin = new Thickness(0, 0, 0, 10)
        };
        var okButton = new Button
        {
            Content = Loc.S.JumpLineButton,
            IsDefault = true,
            MinWidth = 64,
            Margin = new Thickness(0, 0, 6, 0)
        };
        var cancelButton = new Button
        {
            Content = Loc.S.CancelButton,
            IsCancel = true,
            MinWidth = 64
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(input);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = Loc.S.JumpLineButton,
            Owner = this,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("PanelBackBrush"),
            Foreground = (Brush)FindResource("TextBrush")
        };

        okButton.Click += (_, _) =>
        {
            if (JumpToLine(input.Text))
            {
                dialog.DialogResult = true;
            }
        };
        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        dialog.ShowDialog();
    }

    private bool JumpToLine(string text)
    {
        var document = _document;
        if (document is null)
        {
            return false;
        }

        text = text.Replace(",", string.Empty).Trim();
        if (!long.TryParse(text, out var lineNumber))
        {
            _ = ShowErrorAsync(Loc.S.CannotJumpTitle, Loc.S.CannotJumpLine);
            return false;
        }

        lineNumber = Math.Clamp(lineNumber, 1, document.LineCount);
        var maxStartLine = Math.Max(1, document.LineCount - _pageLineCount + 1);
        _highlightedLineNumber = lineNumber;
        LoadPageByLineNumber(Math.Min(lineNumber, maxStartLine), updateScrollBar: true);
        return true;
    }
    private void CancelSearchButton_Click(object sender, RoutedEventArgs e)
    {
        _searchCts?.Cancel();
    }

    private void ExportSearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_searchResults.Count == 0 || _document is null)
        {
            return;
        }

        var baseName = Path.GetFileNameWithoutExtension(_document.FilePath);
        var keyword = _activeSearchCriteria?.Pattern ?? string.Empty;
        var defaultFileName = $"{baseName}_{keyword}.log";

        var dialog = new SaveFileDialog
        {
            Filter = Loc.S.ExportFilter,
            FileName = defaultFileName,
            DefaultExt = ".log",
            Title = Loc.S.ExportButtonTip
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            using var writer = new StreamWriter(dialog.FileName, false, Encoding.UTF8);
            foreach (var result in _searchResults)
            {
                writer.Write(result.LineNumber);
                writer.Write('\t');
                writer.WriteLine(result.Text);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AdvancedSearchButton_Click(object sender, RoutedEventArgs e)
    {
        AdvancedSearchPopup.IsOpen = true;
    }

    private void AdvancedSearchPopup_Opened(object? sender, EventArgs e)
    {
        if (AdvancedSearchQuery.TryParse(SearchTextBox.Text, out var query))
        {
            PopulateAdvancedPanels(query.Includes, query.Excludes);
        }
        else if (AdvancedIncludePanel.Children.Count == 0 && AdvancedExcludePanel.Children.Count == 0)
        {
            AddKeywordRow(AdvancedIncludePanel, string.Empty);
            AddKeywordRow(AdvancedExcludePanel, string.Empty);
        }
    }

    private void PopulateAdvancedPanels(IReadOnlyList<string> includes, IReadOnlyList<string> excludes)
    {
        AdvancedIncludePanel.Children.Clear();
        AdvancedExcludePanel.Children.Clear();

        foreach (var term in includes)
        {
            AddKeywordRow(AdvancedIncludePanel, term);
        }

        foreach (var term in excludes)
        {
            AddKeywordRow(AdvancedExcludePanel, term);
        }

        if (AdvancedIncludePanel.Children.Count == 0)
        {
            AddKeywordRow(AdvancedIncludePanel, string.Empty);
        }

        if (AdvancedExcludePanel.Children.Count == 0)
        {
            AddKeywordRow(AdvancedExcludePanel, string.Empty);
        }
    }

    private void AddIncludeKeyword_Click(object sender, RoutedEventArgs e)
    {
        AddKeywordRow(AdvancedIncludePanel, string.Empty);
    }

    private void AddExcludeKeyword_Click(object sender, RoutedEventArgs e)
    {
        AddKeywordRow(AdvancedExcludePanel, string.Empty);
    }

    private void AddKeywordRow(Panel host, string text)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textBox = new TextBox { Text = text };
        Grid.SetColumn(textBox, 0);

        var removeButton = new Button
        {
            Content = "×",
            Width = 28,
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = Loc.S.RemoveKeywordTip
        };
        removeButton.Click += (_, _) => host.Children.Remove(row);
        Grid.SetColumn(removeButton, 1);

        row.Children.Add(textBox);
        row.Children.Add(removeButton);
        host.Children.Add(row);
        textBox.Focus();
    }

    private static List<string> CollectKeywords(Panel host)
    {
        var keywords = new List<string>();
        foreach (var child in host.Children)
        {
            if (child is not Grid row)
            {
                continue;
            }

            var textBox = row.Children.OfType<TextBox>().FirstOrDefault();
            var term = textBox?.Text.Trim();
            if (!string.IsNullOrEmpty(term))
            {
                keywords.Add(term);
            }
        }

        return keywords;
    }

    private async void AdvancedSearchRun_Click(object sender, RoutedEventArgs e)
    {
        var includes = CollectKeywords(AdvancedIncludePanel);
        var excludes = CollectKeywords(AdvancedExcludePanel);

        if (includes.Count == 0 && excludes.Count == 0)
        {
            await ShowErrorAsync(Loc.S.CannotSearchTitle, Loc.S.CannotSearchNoKeyword);
            return;
        }

        var badTerm = includes.Concat(excludes).FirstOrDefault(term => !IsAsciiKeyword(term));
        if (badTerm is not null)
        {
            await ShowErrorAsync(Loc.S.CannotSearchTitle, Loc.S.CannotSearchAscii);
            return;
        }

        SearchTextBox.Text = AdvancedSearchQuery.Format(includes, excludes);
        AdvancedSearchPopup.IsOpen = false;
        await StartSearchAsync();
    }

    private void AdvancedSearchClose_Click(object sender, RoutedEventArgs e)
    {
        AdvancedSearchPopup.IsOpen = false;
    }

    private static bool IsAsciiKeyword(string term)
    {
        foreach (var ch in term)
        {
            if (ch > '\x7F')
            {
                return false;
            }
        }

        return true;
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

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var language = LanguageComboBox.SelectedIndex == 1 ? AppLanguage.English : AppLanguage.Chinese;
        if (language == Loc.Current)
        {
            return;
        }

        Loc.SetLanguage(language);
        _settings.Language = Loc.ToCode(language);
        SaveSettings();
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        var s = Loc.S;

        OpenButton.Content = s.OpenButton;
        OpenButton.ToolTip = s.OpenButtonTip;
        RefreshButton.Content = s.RefreshButton;
        RefreshButton.ToolTip = s.RefreshButtonTip;
        UpdateLiveRefreshText();
        EncodingComboBox.ToolTip = s.EncodingTip;
        RecentButton.Content = s.RecentButton;
        RecentButton.ToolTip = s.RecentButtonTip;
        SettingsButton.Content = s.SettingsButton;
        SettingsButton.ToolTip = s.SettingsButtonTip;

        FontLabel.Text = s.FontLabel;
        FontSizeLabel.Text = s.FontSizeLabel;
        LanguageLabel.Text = s.LanguageLabel;
        FileAssocLabel.Text = s.FileAssocLabel;
        FileAssocHintLabel.Text = s.FileAssocHint;
        ApplyFileAssociationButton.Content = s.ApplyAssocButton;
        ApplyFileAssociationButton.ToolTip = s.ApplyAssocButtonTip;

        CaseSensitiveButton.ToolTip = s.CaseSensitiveTip;
        RegexButton.ToolTip = s.RegexTip;
        AdvancedSearchButton.Content = s.AdvancedButton;
        AdvancedSearchButton.ToolTip = s.AdvancedButtonTip;

        AdvancedTitleLabel.Text = s.AdvancedTitle;
        AdvancedHintLabel.Text = s.AdvancedHint;
        IncludeLabel.Text = s.IncludeLabel;
        AddIncludeButton.Content = s.AddIncludeButton;
        ExcludeLabel.Text = s.ExcludeLabel;
        AddExcludeButton.Content = s.AddExcludeButton;
        AdvancedCloseButton.Content = s.CloseButton;
        AdvancedRunButton.Content = s.SearchButton;

        SearchTextBox.ToolTip = s.SearchTextBoxTip;
        SearchHistoryButton.ToolTip = s.SearchHistoryTip;
        PreviousResultButton.ToolTip = s.PreviousResultTip;
        NextResultButton.ToolTip = s.NextResultTip;
        SearchButton.Content = s.SearchButton;
        SearchButton.ToolTip = s.SearchButtonTip;
        CancelSearchButton.Content = s.CancelButton;
        CancelSearchButton.ToolTip = s.CancelButtonTip;
        ExportSearchButton.Content = s.ExportButton;
        ExportSearchButton.ToolTip = s.ExportButtonTip;

        LogCopyMenuItem.Header = s.MenuCopy;
        LogSelectAllMenuItem.Header = s.MenuSelectAll;
        SearchCopyMenuItem.Header = s.MenuCopy;
        SearchSelectAllMenuItem.Header = s.MenuSelectAll;

        UpdateThemeToggleText();

        if (_isSettingsInitialized)
        {
            UpdateVersionText();
        }

        RefreshStatusTexts();
    }

    private void RefreshStatusTexts()
    {
        SearchResultStatusTextBlock.Text = Loc.S.SearchResultCount(_searchResults.Count);
        UpdateMemoryStatus();

        if (_document is null)
        {
            if (!_isOpening && !_isSearching)
            {
                FilePathTextBlock.Text = Loc.S.NoFileOpen;
                SearchStatusTextBlock.Text = Loc.S.Ready;
            }
        }
        else
        {
            UpdateDocumentStatus();
        }
    }

    private void UpdateVersionText()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var versionText = version is null
            ? "1.0.0"
            : $"{version.Major}.{version.Minor}.{version.Build}";
        VersionTextBlock.Text = Loc.S.VersionText(versionText);
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

        UpdateVersionText();

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

        LanguageComboBox.SelectedIndex = Loc.Current == AppLanguage.English ? 1 : 0;

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

    private void SaveRecentFile(string filePath)
    {
        MoveToFront(_settings.RecentFiles, Path.GetFullPath(filePath), MaxRecentFiles, StringComparer.OrdinalIgnoreCase);
        SaveSettings();
        UpdateControlState();
    }

    private void SaveSearchHistory(string pattern)
    {
        MoveToFront(_settings.SearchHistory, pattern, MaxSearchHistory, StringComparer.Ordinal);
        SaveSettings();
        RefreshSearchHistoryItems();
    }

    private void RefreshSearchHistoryItems()
    {
        UpdateControlState();
    }

    private static void MoveToFront(List<string> items, string value, int maxCount, StringComparer comparer)
    {
        value = value.Trim();
        if (value.Length == 0)
        {
            return;
        }

        items.RemoveAll(item => comparer.Equals(item, value));
        items.Insert(0, value);
        if (items.Count > maxCount)
        {
            items.RemoveRange(maxCount, items.Count - maxCount);
        }
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

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateSearchResultsMaxHeight();
    }

    // 限制搜索结果栏的最高高度，确保日志主区域至少保留 MinHeight，避免底部栏被挤出屏幕
    private void UpdateSearchResultsMaxHeight()
    {
        if (RootGrid.ActualHeight <= 0)
        {
            return;
        }

        var rows = RootGrid.RowDefinitions;
        var reserved = rows[0].Height.Value + rows[2].Height.Value + rows[4].Height.Value + MainLogRow.MinHeight;
        var max = RootGrid.ActualHeight - reserved;
        SearchResultsRow.MaxHeight = Math.Max(SearchResultsRow.MinHeight, max);
    }

    private void SearchContentBox_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateSearchPageLineCount(e.NewSize.Height);
    }

    private void UpdateSearchPageLineCount(double viewportHeight)
    {
        var lineHeight = GetSearchContentLineHeight();
        var reserve = _searchReserveHScroll ? HorizontalScrollBarHeight : 0;
        var usableHeight = viewportHeight - SearchContentBox.Padding.Top - SearchContentBox.Padding.Bottom - reserve;
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
        if (_isRefreshingLive)
        {
            return;
        }

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

        _selectedSearchResultIndex = resultIndex;
        SelectBoxLine(SearchContentBox, lineInView);
        NavigateToSearchResult(_searchResults[resultIndex]);
    }

    private void NavigateSearchResult(int delta)
    {
        if (_searchResults.Count == 0)
        {
            return;
        }

        var index = _selectedSearchResultIndex;
        if (index < 0 || index >= _searchResults.Count)
        {
            index = FindSearchResultNearCurrentLine(delta);
        }
        else
        {
            index = (index + delta + _searchResults.Count) % _searchResults.Count;
        }

        NavigateToSearchResultIndex(index);
    }

    private int FindSearchResultNearCurrentLine(int delta)
    {
        var lineNumber = _highlightedLineNumber ?? _currentLineNumber;
        if (delta >= 0)
        {
            for (var i = 0; i < _searchResults.Count; i++)
            {
                if (_searchResults[i].LineNumber >= lineNumber)
                {
                    return i;
                }
            }

            return 0;
        }

        for (var i = _searchResults.Count - 1; i >= 0; i--)
        {
            if (_searchResults[i].LineNumber <= lineNumber)
            {
                return i;
            }
        }

        return _searchResults.Count - 1;
    }

    private void NavigateToSearchResultIndex(int index)
    {
        index = Math.Clamp(index, 0, _searchResults.Count - 1);
        _selectedSearchResultIndex = index;

        if (index < _searchTopIndex || index >= _searchTopIndex + _searchPageLineCount)
        {
            var maxTopIndex = Math.Max(0, _searchResults.Count - _searchPageLineCount);
            _searchTopIndex = Math.Clamp(index - _searchPageLineCount / 2, 0, maxTopIndex);
            RefreshSearchTextBoxes();
            UpdateSearchScrollThumb();
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            var lineInView = _selectedSearchResultIndex - _searchTopIndex;
            SelectBoxLine(SearchContentBox, lineInView);
        }));

        NavigateToSearchResult(_searchResults[index]);
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
        var reserve = _logReserveHScroll ? HorizontalScrollBarHeight : 0;
        var usableHeight = viewportHeight - LogContentBox.Padding.Top - LogContentBox.Padding.Bottom - reserve;
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

    private static ScrollViewer? FindContentScrollViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            var nested = FindContentScrollViewer(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void ResetLogHorizontalScroll()
    {
        _logMaxContentWidth = 0;
        _logReserveHScroll = false;
        LogContentBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
    }

    private void ResetSearchHorizontalScroll()
    {
        _searchMaxContentWidth = 0;
        _searchReserveHScroll = false;
        SearchContentBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
    }

    private void ScheduleLogHorizontalScrollState()
    {
        if (_logHScrollStatePending)
        {
            return;
        }

        _logHScrollStatePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateLogHorizontalScrollState));
    }

    private void ScheduleSearchHorizontalScrollState()
    {
        if (_searchHScrollStatePending)
        {
            return;
        }

        _searchHScrollStatePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateSearchHorizontalScrollState));
    }

    private void UpdateLogHorizontalScrollState()
    {
        _logHScrollStatePending = false;
        _logContentScrollViewer ??= FindContentScrollViewer(LogContentBox);
        var scrollViewer = _logContentScrollViewer;
        if (scrollViewer is null)
        {
            return;
        }

        if (scrollViewer.ExtentWidth > _logMaxContentWidth)
        {
            _logMaxContentWidth = scrollViewer.ExtentWidth;
        }

        var needScroll = _logMaxContentWidth > scrollViewer.ViewportWidth + 0.5;
        LogContentBox.HorizontalScrollBarVisibility =
            needScroll ? ScrollBarVisibility.Visible : ScrollBarVisibility.Auto;

        if (needScroll != _logReserveHScroll)
        {
            _logReserveHScroll = needScroll;
            UpdatePageLineCount(LogContentBox.ActualHeight);
        }
    }

    private void UpdateSearchHorizontalScrollState()
    {
        _searchHScrollStatePending = false;
        _searchContentScrollViewer ??= FindContentScrollViewer(SearchContentBox);
        var scrollViewer = _searchContentScrollViewer;
        if (scrollViewer is null)
        {
            return;
        }

        if (scrollViewer.ExtentWidth > _searchMaxContentWidth)
        {
            _searchMaxContentWidth = scrollViewer.ExtentWidth;
        }

        var needScroll = _searchMaxContentWidth > scrollViewer.ViewportWidth + 0.5;
        SearchContentBox.HorizontalScrollBarVisibility =
            needScroll ? ScrollBarVisibility.Visible : ScrollBarVisibility.Auto;

        if (needScroll != _searchReserveHScroll)
        {
            _searchReserveHScroll = needScroll;
            UpdateSearchPageLineCount(SearchContentBox.ActualHeight);
        }
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
        _liveRefreshTimer.Stop();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _document?.Dispose();
    }

    private void SetLiveRefreshEnabled(bool enabled)
    {
        _isLiveRefreshEnabled = enabled && _document is not null;
        if (_isLiveRefreshEnabled)
        {
            _liveRefreshTimer.Start();
        }
        else
        {
            _liveRefreshTimer.Stop();
        }

        if (LiveRefreshButton.IsChecked != _isLiveRefreshEnabled)
        {
            LiveRefreshButton.IsChecked = _isLiveRefreshEnabled;
        }

        UpdateLiveRefreshText();
        UpdateControlState();
    }

    private async Task RefreshLiveAsync()
    {
        var document = _document;
        if (!_isLiveRefreshEnabled || document is null || _isOpening || _isSearching || _isRefreshingLive)
        {
            return;
        }

        _isRefreshingLive = true;
        var followTail = IsLogViewAtTail(document);

        try
        {
            var appendResult = await Task.Run(document.AppendNewContent);
            if (appendResult.IsTruncated)
            {
                await OpenFileAsync(document.FilePath, document.EncodingKind, Math.Max(1, _currentLineNumber));
                return;
            }

            if (!appendResult.HasNewContent)
            {
                return;
            }

            var firstChangedLine = Math.Max(1, appendResult.OldLineCount);
            UpdateLineNumberColumnWidth();
            ConfigureScrollBar();

            if (followTail)
            {
                LoadTailPage();
            }
            else if (CurrentLogPageTouches(firstChangedLine))
            {
                ReloadCurrentPage();
            }
            else
            {
                UpdateDocumentStatus();
            }

            await RefreshLiveSearchResultsAsync(firstChangedLine);
        }
        catch (Exception ex)
        {
            SearchStatusTextBlock.Text = Loc.S.ReadFailedTitle;
            await ShowErrorAsync(Loc.S.ReadFailedTitle, DescribeException(ex));
            SetLiveRefreshEnabled(false);
        }
        finally
        {
            _isRefreshingLive = false;
            UpdateControlState();
        }
    }

    private async Task RefreshLiveSearchResultsAsync(long firstLineNumber)
    {
        var document = _document;
        var criteria = _activeSearchCriteria;
        if (document is null || criteria is null)
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        if (criteria.AdvancedQuery is not null)
        {
            await document.AdvancedSearchLinesAsync(
                firstLineNumber,
                criteria.AdvancedQuery.Includes,
                criteria.AdvancedQuery.Excludes,
                criteria.CaseSensitive,
                AddLiveSearchResultBatch,
                cts.Token);
            return;
        }

        await document.SearchLinesAsync(
            firstLineNumber,
            criteria.Pattern,
            criteria.UseRegex,
            criteria.CaseSensitive,
            AddLiveSearchResultBatch,
            cts.Token);
    }

    private bool IsLogViewAtTail(LogFileDocument document)
    {
        return _logLines.Count == 0 || _nextOffset >= document.FileSize;
    }

    private bool CurrentLogPageTouches(long lineNumber)
    {
        if (_logLines.Count == 0)
        {
            return false;
        }

        return _logLines[0].LineNumber <= lineNumber && _logLines[^1].LineNumber >= lineNumber;
    }

    private void LoadTailPage()
    {
        if (_document is null)
        {
            return;
        }

        var startLine = Math.Max(1, _document.LineCount - _pageLineCount + 1);
        LoadPageByLineNumber(startLine, updateScrollBar: true);
    }

    private void ApplyTheme(AppTheme theme)
    {
        _currentTheme = theme;

        foreach (var (brushKey, colors) in ThemeBrushes)
        {
            SetBrushColor(brushKey, theme == AppTheme.Dark ? colors.Dark : colors.Light);
        }

        ApplyInactiveSelectionHighlight(theme);

        UpdateThemeToggleText();

        ApplyNativeTitleBarTheme();
    }

    private void UpdateThemeToggleText()
    {
        _isUpdatingThemeToggle = true;
        ThemeToggleButton.IsChecked = _currentTheme == AppTheme.Light;
        ThemeToggleButton.Content = _currentTheme == AppTheme.Dark ? Loc.S.ThemeDark : Loc.S.ThemeLight;
        ThemeToggleButton.ToolTip = _currentTheme == AppTheme.Dark
            ? Loc.S.ThemeTipDark
            : Loc.S.ThemeTipLight;
        _isUpdatingThemeToggle = false;
    }

    private void UpdateLiveRefreshText()
    {
        LiveRefreshButton.Content = _isLiveRefreshEnabled ? Loc.S.LiveRefreshOn : Loc.S.LiveRefreshOff;
        LiveRefreshButton.ToolTip = _isLiveRefreshEnabled
            ? Loc.S.LiveRefreshOnTip
            : Loc.S.LiveRefreshOffTip;
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
        SearchStatusTextBlock.Text = Loc.S.Loading;
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
            ClearSearchResults();
            _activeSearchCriteria = null;
            ResetLogHorizontalScroll();
            ResetSearchHorizontalScroll();
            RefreshLogTextBoxes();
            ClearSearchTextBoxes();
            ConfigureScrollBar();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            var stopwatch = Stopwatch.StartNew();
            var newDocument = await Task.Run(() => LogFileDocument.Open(filePath, encodingOverride));
            stopwatch.Stop();
            _document = newDocument;
            SaveRecentFile(filePath);

            SelectEncoding(newDocument.EncodingKind);
            UpdateLineNumberColumnWidth();
            SearchResultStatusTextBlock.Text = Loc.S.SearchResultCount(0);
            SearchStatusTextBlock.Text = Loc.S.LoadDone(stopwatch.Elapsed.TotalSeconds);

            ConfigureScrollBar();
            LoadPageByLineNumber(Math.Max(1, startLineNumber), updateScrollBar: true);
        }
        catch (Exception ex)
        {
            SearchStatusTextBlock.Text = Loc.S.OpenFailedTitle;
            await ShowErrorAsync(Loc.S.OpenFailedTitle, DescribeException(ex));
            if (_document is null)
            {
                SetLiveRefreshEnabled(false);
            }

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
            _ = ShowErrorAsync(Loc.S.ReadFailedTitle, DescribeException(ex));
        }
    }

    private async Task StartSearchAsync()
    {
        var document = _document;
        if (document is null || _isSearching || _isOpening || _isRefreshingLive)
        {
            return;
        }

        var pattern = SearchTextBox.Text;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            await ShowErrorAsync(Loc.S.CannotSearchTitle, Loc.S.CannotSearchEmpty);
            return;
        }

        AdvancedSearchQuery? advancedQuery = null;
        if (AdvancedSearchQuery.TryParse(pattern, out var parsedAdvanced))
        {
            var badTerm = parsedAdvanced.Includes
                .Concat(parsedAdvanced.Excludes)
                .FirstOrDefault(term => !IsAsciiKeyword(term));
            if (badTerm is not null)
            {
                await ShowErrorAsync(Loc.S.CannotSearchTitle, Loc.S.CannotSearchAscii);
                return;
            }

            advancedQuery = parsedAdvanced;
        }

        var criteria = new SearchCriteria(
            pattern,
            RegexButton.IsChecked == true,
            CaseSensitiveButton.IsChecked == true,
            advancedQuery);

        var searchCts = new CancellationTokenSource();
        _searchCts = searchCts;
        _isSearching = true;
        ClearSearchResults();
        _activeSearchCriteria = null;
        ResetSearchHorizontalScroll();
        ClearSearchTextBoxes();
        SearchProgressBar.Value = 0;
        SearchProgressBar.Visibility = Visibility.Visible;
        SearchResultStatusTextBlock.Text = Loc.S.SearchResultCount(0);
        SearchStatusTextBlock.Text = Loc.S.Searching;
        UpdateControlState();

        var progress = new Progress<LogSearchProgress>(UpdateSearchProgress);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var searchTask = advancedQuery is not null
                ? document.AdvancedSearchAsync(
                    advancedQuery.Includes,
                    advancedQuery.Excludes,
                    criteria.CaseSensitive,
                    AddSearchResultBatch,
                    progress,
                    searchCts.Token)
                : document.SearchAsync(
                    criteria.Pattern,
                    criteria.UseRegex,
                    criteria.CaseSensitive,
                    AddSearchResultBatch,
                    progress,
                    searchCts.Token);

            var summary = await searchTask;

            stopwatch.Stop();
            _activeSearchCriteria = criteria;
            SaveSearchHistory(criteria.Pattern);
            SearchProgressBar.Value = 100;
            SearchStatusTextBlock.Text = Loc.S.SearchDone(summary.MatchCount, stopwatch.Elapsed.TotalSeconds);
            SearchResultStatusTextBlock.Text = Loc.S.SearchResultCount(summary.MatchCount);
        }
        catch (OperationCanceledException)
        {
            SearchStatusTextBlock.Text = Loc.S.SearchCancelled(_searchResults.Count);
            SearchResultStatusTextBlock.Text = Loc.S.SearchResultCount(_searchResults.Count);
        }
        catch (ArgumentException ex)
        {
            SearchStatusTextBlock.Text = Loc.S.SearchFailedTitle;
            await ShowErrorAsync(Loc.S.SearchFailedTitle, DescribeException(ex));
        }
        catch (Exception ex)
        {
            SearchStatusTextBlock.Text = Loc.S.SearchFailedTitle;
            await ShowErrorAsync(Loc.S.SearchFailedTitle, DescribeException(ex));
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
        AddSearchResultBatchCore(batch, followWhenAtEnd: false);
    }

    private void AddLiveSearchResultBatch(IReadOnlyList<LogSearchResult> batch)
    {
        AddSearchResultBatchCore(batch, followWhenAtEnd: true);
    }

    private void AddSearchResultBatchCore(IReadOnlyList<LogSearchResult> batch, bool followWhenAtEnd)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            var wasAtEnd = _searchResults.Count == 0 ||
                           _searchTopIndex >= Math.Max(0, _searchResults.Count - _searchPageLineCount);
            var added = new List<LogSearchResult>(batch.Count);
            var refreshedExisting = false;

            foreach (var result in batch)
            {
                if (_searchResultLineNumbers.Add(result.LineNumber))
                {
                    _searchResultsByLineNumber[result.LineNumber] = result;
                    added.Add(result);
                }
                else if (_searchResultsByLineNumber.TryGetValue(result.LineNumber, out var existing))
                {
                    existing.InvalidateText();
                    refreshedExisting = true;
                }
            }

            if (added.Count > 0)
            {
                _searchResults.AddRange(added);
                if (followWhenAtEnd && wasAtEnd)
                {
                    _searchTopIndex = Math.Max(0, _searchResults.Count - _searchPageLineCount);
                }
            }

            if (added.Count > 0 || refreshedExisting)
            {
                RefreshSearchTextBoxes();
                UpdateSearchScrollAvailability();
                UpdateSearchScrollThumb();
                SearchResultStatusTextBlock.Text = Loc.S.SearchResultCount(_searchResults.Count);
                UpdateControlState();
            }
        }, DispatcherPriority.Background);
    }

    private void ClearSearchResults()
    {
        _searchResults.Clear();
        _searchResultLineNumbers.Clear();
        _searchResultsByLineNumber.Clear();
        _selectedSearchResultIndex = -1;
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
        ScheduleLogHorizontalScrollState();
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
        ScheduleSearchHorizontalScrollState();
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
        SearchStatusTextBlock.Text = Loc.S.SearchingCount(progress.MatchCount);
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
        MemoryStatusTextBlock.Text = _document is null
            ? Loc.S.MemoryIdle(FormatBytes(available), FormatBytes(LogFileDocument.MaxFileSize))
            : Loc.S.MemoryActive(FormatBytes(available), FormatBytes(_document.MemoryUsage));
    }

    private void ResetStatus()
    {
        Title = "LogRAM";
        FilePathTextBlock.Text = Loc.S.NoFileOpen;
        FileSizeTextBlock.Text = string.Empty;
        EncodingStatusTextBlock.Text = string.Empty;
        SearchStatusTextBlock.Text = Loc.S.Ready;
        SearchResultStatusTextBlock.Text = Loc.S.SearchResultCount(0);
        SearchProgressBar.Visibility = Visibility.Collapsed;
        ConfigureScrollBar();
    }

    private void UpdateDocumentStatus()
    {
        if (_document is null)
        {
            ResetStatus();
            UpdateMemoryStatus();
            return;
        }

        FilePathTextBlock.Text = _document.FilePath;
        Title = Path.GetFileName(_document.FilePath);
        FileSizeTextBlock.Text = Loc.S.DocStatus(FormatBytes(_document.FileSize), _document.LineCount, _currentLineNumber);
        EncodingStatusTextBlock.Text = FormatEncoding(_document.EncodingKind);
        UpdateMemoryStatus();
    }

    private void UpdateControlState()
    {
        var hasDocument = _document is not null;
        var canChangeDocument = !_isOpening && !_isSearching && !_isRefreshingLive;

        OpenButton.IsEnabled = canChangeDocument;
        RecentButton.IsEnabled = canChangeDocument && _settings.RecentFiles.Count > 0;
        RefreshButton.IsEnabled = hasDocument && canChangeDocument;
        LiveRefreshButton.IsEnabled = hasDocument && !_isOpening;
        EncodingComboBox.IsEnabled = hasDocument && canChangeDocument;
        SearchButton.IsEnabled = hasDocument && !_isSearching && !_isOpening && !_isRefreshingLive;
        SearchHistoryButton.IsEnabled = _settings.SearchHistory.Count > 0 && !_isOpening && !_isRefreshingLive;
        AdvancedSearchButton.IsEnabled = hasDocument && !_isSearching && !_isOpening && !_isRefreshingLive;
        PreviousResultButton.IsEnabled = _searchResults.Count > 0 && !_isOpening && !_isRefreshingLive;
        NextResultButton.IsEnabled = _searchResults.Count > 0 && !_isOpening && !_isRefreshingLive;
        CancelSearchButton.IsEnabled = _isSearching;
        UpdateLogScrollAvailability();
        UpdateLogScrollThumb();
    }

    private bool CanScrollLog()
    {
        return _document is not null && !_isOpening && !_isRefreshingLive && _document.LineCount > _pageLineCount;
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

            var boundList = string.Join(Loc.S.AssocSeparator, _settings.FileAssociations.DefaultIfEmpty(Loc.S.AssocNone));
            MessageBox.Show(
                this,
                Loc.S.AssocUpdated(boundList),
                Loc.S.AssocDoneTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _ = ShowErrorAsync(Loc.S.AssocFailedTitle, DescribeException(ex));
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
            progKey.SetValue("FriendlyTypeName", Loc.S.FriendlyTypeName(extension), RegistryValueKind.String);
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
            FileNotFoundException => Loc.S.DescribeFileNotFound,
            UnauthorizedAccessException => Loc.S.DescribeUnauthorized,
            InvalidOperationException when ex.Message.Contains("available memory limit", StringComparison.OrdinalIgnoreCase) => Loc.S.DescribeMemoryLimit(FormatBytes(LogFileDocument.MaxFileSize)),
            OutOfMemoryException => Loc.S.DescribeOutOfMemory,
            ArgumentException when ex.Message.Contains("pattern", StringComparison.OrdinalIgnoreCase) => Loc.S.DescribeEmptyPattern,
            _ => ex.Message
        };
    }
}
