using System;

namespace LogRAM;

public enum AppLanguage
{
    Chinese,
    English
}

public sealed class LocalizedStrings
{
    private readonly bool _en;

    private LocalizedStrings(bool english) => _en = english;

    public static readonly LocalizedStrings Chinese = new(false);
    public static readonly LocalizedStrings English = new(true);

    private string T(string zh, string en) => _en ? en : zh;

    public string OpenButton => T("打开", "Open");
    public string OpenButtonTip => T("打开日志文件", "Open log file");
    public string RefreshButton => T("刷新", "Refresh");
    public string RefreshButtonTip => T("重新读取当前文件", "Reload current file");
    public string LiveRefreshOff => T("实时", "Live");
    public string LiveRefreshOn => T("停止", "Stop");
    public string LiveRefreshOffTip => T("实时增量刷新当前文件", "Incrementally refresh the current file");
    public string LiveRefreshOnTip => T("停止实时刷新", "Stop live refresh");
    public string EncodingTip => T("编码", "Encoding");
    public string RecentButton => T("最近", "Recent");
    public string RecentButtonTip => T("打开最近文件", "Open recent file");
    public string CloseTabTip => T("关闭标签页", "Close tab");
    public string JumpLineButton => T("跳转", "Go");
    public string SettingsButton => T("设置", "Settings");
    public string SettingsButtonTip => T("调整字体、字号、语言与内存", "Adjust font, size, language and memory");

    public string FontLabel => T("字体", "Font");
    public string FontSizeLabel => T("字号", "Font size");
    public string LanguageLabel => T("语言", "Language");
    public string InactiveMemoryReleaseLabel => T("失焦释放内存（分钟）", "Release memory when inactive (minutes)");
    public string Never => T("永不", "Never");

    public string FileAssocLabel => T("文件类型绑定", "File associations");
    public string FileAssocHint => T(
        "绑定后可通过右键或双击直接使用 LogRAM 打开以下类型文件",
        "After binding, you can open these file types with LogRAM via right-click or double-click");
    public string ApplyAssocButton => T("应用绑定", "Apply");
    public string ApplyAssocButtonTip => T("将选中的文件类型绑定到 LogRAM", "Associate the selected file types with LogRAM");

    public string ThemeDark => T("深色", "Dark");
    public string ThemeLight => T("浅色", "Light");
    public string ThemeTipDark => T("当前深色模式，点击切换浅色模式", "Dark mode, click to switch to light");
    public string ThemeTipLight => T("当前浅色模式，点击切换深色模式", "Light mode, click to switch to dark");

    public string CaseSensitiveTip => T("区分大小写", "Match case");
    public string RegexTip => T("正则表达式", "Regular expression");
    public string AdvancedButton => T("高级", "Advanced");
    public string AdvancedButtonTip => T("高级搜索：自定义包含/排除关键词", "Advanced search: custom include/exclude keywords");

    public string AdvancedTitle => T("高级搜索", "Advanced search");
    public string AdvancedHint => T(
        "命中条件：包含任一“包含”关键词，且不包含任何“排除”关键词。仅支持 ASCII 关键词。",
        "Match rule: contains any \"include\" keyword and none of the \"exclude\" keywords. ASCII keywords only.");
    public string IncludeLabel => T("包含任一（OR）", "Include any (OR)");
    public string AddIncludeButton => T("+ 添加包含", "+ Add include");
    public string ExcludeLabel => T("排除任一（NOT）", "Exclude any (NOT)");
    public string AddExcludeButton => T("+ 添加排除", "+ Add exclude");
    public string CloseButton => T("关闭", "Close");
    public string RemoveKeywordTip => T("删除该关键词", "Remove this keyword");

    public string SearchButton => T("搜索", "Search");
    public string SearchButtonTip => T("开始搜索", "Start search");
    public string SearchTextBoxTip => T("搜索文本或正则", "Search text or regex");
    public string SearchHistoryTip => T("搜索历史", "Search history");
    public string PreviousResultTip => T("上一个搜索结果（Shift+F3）", "Previous result (Shift+F3)");
    public string NextResultTip => T("下一个搜索结果（F3）", "Next result (F3)");
    public string CancelButton => T("取消", "Cancel");
    public string CancelButtonTip => T("取消搜索", "Cancel search");

    public string ExportButton => T("导出", "Export");
    public string ExportButtonTip => T("导出搜索结果", "Export search results");
    public string ExportFilter => T("日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*", "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*");

    public string MenuCopy => T("复制", "Copy");
    public string MenuSelectAll => T("全选", "Select all");

    public string OpenDialogFilter => T("所有文件 (*.*)|*.*", "All files (*.*)|*.*");
    public string OpenDialogTitle => T("打开日志文件", "Open log file");

    public string Loading => T("加载中", "Loading");
    public string ReloadingAfterInactivity(int percent) => _en
        ? $"Reloading logs, please wait… {percent}%"
        : $"正在重新加载日志，请稍候… {percent}%";
    public string Searching => T("搜索中", "Searching");
    public string Ready => T("就绪", "Ready");
    public string NoFileOpen => T("未打开文件", "No file opened");

    public string OpenFailedTitle => T("打开失败", "Open failed");
    public string ReadFailedTitle => T("读取失败", "Read failed");
    public string SearchFailedTitle => T("搜索失败", "Search failed");
    public string CannotSearchTitle => T("无法搜索", "Cannot search");
    public string CannotJumpTitle => T("无法跳转", "Cannot jump");
    public string AssocDoneTitle => T("绑定完成", "Done");
    public string AssocFailedTitle => T("绑定失败", "Binding failed");

    public string CannotSearchEmpty => T("请输入搜索条件。", "Please enter a search query.");
    public string CannotSearchNoKeyword => T("请至少填写一个包含或排除关键词。", "Please add at least one include or exclude keyword.");
    public string CannotSearchAscii => T("高级搜索关键词仅支持 ASCII 字符。", "Advanced search keywords support ASCII characters only.");
    public string CannotJumpLine => T("请输入有效行号。", "Please enter a valid line number.");

    public string AssocNone => T("无", "None");
    public string AssocSeparator => T("、", ", ");

    public string DescribeFileNotFound => T("文件不存在。", "The file does not exist.");
    public string DescribeUnauthorized => T("没有权限访问该文件。", "You do not have permission to access this file.");
    public string DescribeOutOfMemory => T("内存不足，无法构造日志索引。", "Out of memory while building the log index.");
    public string DescribeEmptyPattern => T("搜索条件不能为空。", "The search query cannot be empty.");

    public string VersionText(string version) => _en ? $"Version {version}" : $"版本 {version}";

    public string SearchResultCount(long count) => _en
        ? $"Results: {count:n0}"
        : $"搜索结果：{count:n0} 条";

    public string LoadDone(double seconds) => _en
        ? $"Loaded in {seconds:0.00}s"
        : $"加载完成：{seconds:0.00}s";

    public string SearchDone(long count, double seconds) => _en
        ? $"Done: {count:n0}  {seconds:0.00}s"
        : $"完成：{count:n0} 条  {seconds:0.00}s";

    public string SearchCancelled(long count) => _en
        ? $"Cancelled: {count:n0}"
        : $"已取消：{count:n0} 条";

    public string SearchingCount(long count) => _en
        ? $"Searching: {count:n0}"
        : $"搜索中：{count:n0} 条";

    public string MemoryIdle(string available, string max) => _en
        ? $"Free memory: {available} · Max openable: {max}"
        : $"剩余内存：{available} · 可打开上限：{max}";

    public string MemoryActive(string available, string used) => _en
        ? $"Free memory: {available} · In use: {used}"
        : $"剩余内存：{available} · 当前占用：{used}";

    public string DocStatus(string size, long lineCount, long currentLine) => _en
        ? $"{size}  {lineCount:n0} lines  at {currentLine:n0}"
        : $"{size}  {lineCount:n0} 行  当前 {currentLine:n0}";

    public string AssocUpdated(string boundList) => _en
        ? $"File associations updated.\n\nCurrent defaults: {boundList}\nDouble-clicking these file types will open them with LogRAM."
        : $"文件类型绑定已更新。\n\n当前默认绑定：{boundList}\n双击这些类型的文件将自动使用 LogRAM 打开。";

    public string FriendlyTypeName(string extension) => _en
        ? $"LogRAM {extension} log file"
        : $"LogRAM {extension} 日志文件";

    public string DescribeMemoryLimit(string max) => _en
        ? $"The log file exceeds the current available memory limit (about {max})."
        : $"日志文件超过了当前可用内存上限（约 {max}）。";
}

public static class Loc
{
    public static AppLanguage Current { get; private set; } = AppLanguage.Chinese;

    public static LocalizedStrings S { get; private set; } = LocalizedStrings.Chinese;

    public static void SetLanguage(AppLanguage language)
    {
        Current = language;
        S = language == AppLanguage.English ? LocalizedStrings.English : LocalizedStrings.Chinese;
    }

    public static AppLanguage Parse(string? code) =>
        string.Equals(code, "en", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.English
            : AppLanguage.Chinese;

    public static string ToCode(AppLanguage language) =>
        language == AppLanguage.English ? "en" : "zh";
}
