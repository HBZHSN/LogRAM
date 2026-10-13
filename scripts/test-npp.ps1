param([Parameter(Mandatory)] [string]$NotepadDirectory, [string]$LargeLog, [long]$ExpectedLines = 0, [long]$ExpectedMatches = -1, [switch]$Dark, [switch]$AutoLog)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$evidence = Join-Path $repo 'LogRAM.Npp\bin\integration\evidence'
if (!(Test-Path -LiteralPath (Join-Path $NotepadDirectory 'doLocalConf.xml'))) { throw 'Use a dedicated portable Notepad++ directory for this test.' }
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class NppTestNative {
    public delegate bool EnumProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern IntPtr GetMenu(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetSubMenu(IntPtr menu, int position);
    [DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] public static extern uint GetMenuItemID(IntPtr menu, int position);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetMenuString(IntPtr menu, uint item, StringBuilder text, int count, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="SendMessageW")] public static extern IntPtr SendText(IntPtr window, uint message, IntPtr wparam, string text);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr window, IntPtr rect, IntPtr region, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr window);
    public struct Rect { public int Left, Top, Right, Bottom; }
    public static IntPtr WindowFor(int processId) {
        IntPtr found=IntPtr.Zero;
        EnumWindows((window, parameter)=> { uint pid; GetWindowThreadProcessId(window,out pid);
            var text=new StringBuilder(256); GetClassName(window,text,256);
            if(pid==processId && text.ToString()=="Notepad++") { found=window; return false; } return true; },IntPtr.Zero);
        return found;
    }
    public static IntPtr DialogFor(int processId) {
        IntPtr found=IntPtr.Zero;
        EnumWindows((window, parameter)=> { uint pid; GetWindowThreadProcessId(window,out pid);
            var text=new StringBuilder(256); GetClassName(window,text,256);
            if(pid==processId && text.ToString()=="#32770") { found=window; return false; } return true; },IntPtr.Zero);
        return found;
    }
    public static IntPtr ChildNamed(IntPtr parent, string name) {
        IntPtr found=IntPtr.Zero;
        EnumChildWindows(parent,(window, parameter)=> { var text=new StringBuilder(256); GetWindowText(window,text,256);
            if(text.ToString()==name) found=window; return true; },IntPtr.Zero);
        return found;
    }
    public static IntPtr FileNameEditor(IntPtr dialog) {
        IntPtr found=IntPtr.Zero;
        EnumChildWindows(dialog,(window, parameter)=> { var cls=new StringBuilder(256); GetClassName(window,cls,256);
            if(cls.ToString()=="Edit") {
                if(GetDlgCtrlID(GetParent(window))==1148) { found=window; return false; }
                if(GetDlgCtrlID(window)==1148 && IsWindowVisible(window)) found=window;
                if(GetDlgCtrlID(window)==1001 && IsWindowVisible(window) && found==IntPtr.Zero) found=window;
            } return true; },IntPtr.Zero);
        return found;
    }
    public static IntPtr VisibleEditor(IntPtr parent) {
        IntPtr found=IntPtr.Zero;
        EnumChildWindows(parent,(window, parameter)=> { var cls=new StringBuilder(256); GetClassName(window,cls,256);
            if(cls.ToString()=="Scintilla" && IsWindowVisible(window)) { found=window; return false; } return true; },IntPtr.Zero);
        return found;
    }
    public static IntPtr ReadOnlyEditor(IntPtr parent) {
        IntPtr found=IntPtr.Zero;
        EnumChildWindows(parent,(window, parameter)=> { var cls=new StringBuilder(256); GetClassName(window,cls,256);
            if(cls.ToString()=="Scintilla" && SendMessage(window,2140,IntPtr.Zero,IntPtr.Zero)!=IntPtr.Zero &&
                SendMessage(window,2006,IntPtr.Zero,IntPtr.Zero).ToInt64()>0) { found=window; return false; } return true; },IntPtr.Zero);
        return found;
    }
    public static bool HasVisibleEditor(IntPtr parent) {
        bool found=false;
        EnumChildWindows(parent,(window, parameter)=> { var cls=new StringBuilder(256); GetClassName(window,cls,256);
            if(cls.ToString()=="Scintilla" && IsWindowVisible(window)) found=true; return true; },IntPtr.Zero);
        return found;
    }
    public static string Dump(int processId) {
        var result=new StringBuilder();
        EnumWindows((window, parameter)=> { uint pid; GetWindowThreadProcessId(window,out pid);
            if(pid!=processId) return true;
            var text=new StringBuilder(1024); var cls=new StringBuilder(256);
            GetWindowText(window,text,1024); GetClassName(window,cls,256);
            result.AppendLine(window+" "+cls+" "+text);
            EnumChildWindows(window,(child, ignored)=> { GetWindowText(child,text,1024); result.AppendLine("  "+text); return true; },IntPtr.Zero);
            return true; },IntPtr.Zero);
        return result.ToString();
    }
    public static uint MenuCommand(IntPtr menu, string name) {
        for(int i=0;i<GetMenuItemCount(menu);i++) {
            var text=new StringBuilder(256); GetMenuString(menu,(uint)i,text,256,0x400);
            if(text.ToString().Split('\t')[0].Replace("&", "")==name) return GetMenuItemID(menu,i);
            var sub=GetSubMenu(menu,i); if(sub!=IntPtr.Zero) { var result=MenuCommand(sub,name); if(result!=0) return result; }
        } return 0;
    }
}
'@
function Wait-Until([scriptblock]$Check, [string]$Description) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        if (& $Check) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out: $Description"
}
function Capture-Window([IntPtr]$Window, [string]$Path) {
    $rect = New-Object NppTestNative+Rect
    [void][NppTestNative]::GetClientRect($Window, [ref]$rect)
    $bitmap = [System.Drawing.Bitmap]::new([Math]::Max(1,$rect.Right), [Math]::Max(1,$rect.Bottom))
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $dc = $graphics.GetHdc()
    try { [void][NppTestNative]::PrintWindow($Window, $dc, 0) }
    finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
    $bitmap.Save($Path)
    $bitmap.Dispose()
}
$sampleName = if ($AutoLog) { '中文 sample.LOG' } else { '中文 sample.txt' }
$log = Join-Path $evidence $sampleName
[IO.File]::WriteAllText($log, "info start`r`nERROR first`r`nwarning retry`r`n中文错误`r`nerror tail", [Text.UTF8Encoding]::new($false))
$executable = Join-Path (Resolve-Path -LiteralPath $NotepadDirectory).Path 'notepad++.exe'
$config = Join-Path (Split-Path -Parent $executable) 'config.xml'
if (Test-Path -LiteralPath $config) {
    $configText = [IO.File]::ReadAllText($config) -replace 'bottomHeight="\d+"', 'bottomHeight="650"'
    $darkEnabled = if ($Dark) { 'yes' } else { 'no' }
    $configText = $configText -replace '(<GUIConfig name="DarkMode" enable=")[^"]+', ('$1' + $darkEnabled)
    $configText = $configText -replace '(<GUIConfig name="Auto-detection">)[^<]+', '$1no'
    $configText = $configText -replace '\s*<PluginDlg\b[^>]*pluginName="LogRAM\.Npp\.dll"[^>]*/>', ''
    [IO.File]::WriteAllText($config, $configText, [Text.UTF8Encoding]::new($false))
}
$pluginSettings = Join-Path (Split-Path -Parent $executable) 'plugins\Config\LogRAM.Npp.json'
if (Test-Path -LiteralPath $pluginSettings) {
    $settings = [IO.File]::ReadAllText($pluginSettings) | ConvertFrom-Json
    $settings.Language = 'zh'
    [IO.File]::WriteAllText($pluginSettings, ($settings | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
}
$hostProcess = Start-Process -FilePath $executable -ArgumentList @('-multiInst','-nosession',('"'+$log+'"')) -WindowStyle Hidden -PassThru
$workerId = 0
try {
    Wait-Until { [NppTestNative]::WindowFor($hostProcess.Id) -ne [IntPtr]::Zero } 'Notepad++ startup'
    $hostWindow = [NppTestNative]::WindowFor($hostProcess.Id)
    [void][NppTestNative]::MoveWindow($hostWindow, -32000, -32000, 1200, 1000, $true)
    [void][NppTestNative]::ShowWindow($hostWindow, 4)
    Wait-Until { [NppTestNative]::MenuCommand([NppTestNative]::GetMenu($hostWindow), 'Open current saved file') -ne 0 } 'LogRAM plugin menu'
    if (!$AutoLog) { Wait-Until {
        $editor = [NppTestNative]::VisibleEditor($hostWindow)
        $editor -ne [IntPtr]::Zero -and [NppTestNative]::SendMessage($editor, 2006, [IntPtr]::Zero, [IntPtr]::Zero).ToInt64() -eq (Get-Item -LiteralPath $log).Length
    } 'initial saved document' }
    else { Wait-Until { [NppTestNative]::ReadOnlyEditor($hostWindow) -ne [IntPtr]::Zero } 'automatically routed read-only log tab' }
    $originalEditor = if ($AutoLog) { [NppTestNative]::ReadOnlyEditor($hostWindow) } else { [NppTestNative]::VisibleEditor($hostWindow) }
    $originalLength = [NppTestNative]::SendMessage($originalEditor, 2006, [IntPtr]::Zero, [IntPtr]::Zero).ToInt64()
    if ($AutoLog -and ($originalLength -ge 512 -or $originalLength -eq (Get-Item -LiteralPath $log).Length)) { throw 'The NPP editor must hold only the LogRAM placeholder.' }
    $note = if ($AutoLog) { '' } else { 'unsaved NPP state' }
    foreach ($character in $note.ToCharArray()) { [void][NppTestNative]::PostMessage($originalEditor, 0x102, [IntPtr][int]$character, [IntPtr]1) }
    Wait-Until { [NppTestNative]::SendMessage($originalEditor, 2006, [IntPtr]::Zero, [IntPtr]::Zero).ToInt64() -eq ($originalLength + $note.Length) } 'unsaved editor fixture'
    $menu = [NppTestNative]::GetMenu($hostWindow)
    $openCurrent = [NppTestNative]::MenuCommand($menu, 'Open current saved file')
    if (!$openCurrent) { throw 'The LogRAM plugin menu is missing.' }
    if (!$AutoLog) { [void][NppTestNative]::SendMessage($hostWindow, 0x111, [IntPtr]$openCurrent, [IntPtr]::Zero) }
    Wait-Until { [NppTestNative]::ChildNamed($hostWindow, 'LogRAM') -ne [IntPtr]::Zero } 'embedded viewer'
    $viewer = [NppTestNative]::ChildNamed($hostWindow, 'LogRAM')
    [uint32]$workerId = 0
    [void][NppTestNative]::GetWindowThreadProcessId($viewer, [ref]$workerId)
    if ($workerId -eq $hostProcess.Id -or $workerId -eq 0) { throw 'Viewer was not hosted in its worker process.' }
    $worker = Get-Process -Id $workerId
    if (!$worker.Path.EndsWith('LogRAM.Npp.Viewer.exe')) { throw 'Unexpected viewer process.' }
    $panel = [NppTestNative]::GetParent($viewer)
    if ([NppTestNative]::GetParent($panel) -ne $hostWindow) { throw 'The LogRAM workspace does not occupy the editor area directly.' }
    if ([NppTestNative]::HasVisibleEditor($hostWindow)) { throw 'An extra Notepad++ editor pane is still visible.' }
    Start-Sleep -Milliseconds 1500
    Capture-Window $viewer (Join-Path $evidence 'viewer.png')
    Capture-Window $hostWindow (Join-Path $evidence 'npp.png')
    $automation = [System.Windows.Automation.AutomationElement]::FromHandle($viewer)
    function Find-Element([string]$Name, $Type = $null) {
        $names = @{
            '搜索条件' = 'Search query'; '搜索' = 'Search'; '实时' = 'Live'; '跳转行号' = 'Go to line number';
            '跳转' = 'Go'; '导出' = 'Export'; '导出完成' = 'Export complete'; '正则' = 'Regex'; '取消' = 'Cancel';
            '刷新' = 'Refresh'; '上一结果' = 'Previous'; '下一结果' = 'Next'; '日志原文' = 'Source log';
            '搜索结果列表' = 'Search result list'
        }
        if ((Find-ElementName 'Search query' ([System.Windows.Automation.ControlType]::Edit)) -ne $null) {
            if ($names.ContainsKey($Name)) { $Name = $names[$Name] }
            else { $Name = $Name.Replace(' 行 | ', ' lines | ').Replace(' 条结果', ' matches') }
        }
        return Find-ElementName $Name $Type
    }
    function Find-ElementName([string]$Name, $Type = $null) {
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
        if ($Type) { $condition = [System.Windows.Automation.AndCondition]::new($condition, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $Type)) }
        return $automation.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    }
    function Invoke-Control([string]$Name) {
        $element = Find-Element $Name
        if (!$element) { throw "Control missing: $Name" }
        if (!$element.Current.IsEnabled) { throw "Control disabled: $Name" }
        $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    function Set-DialogPath([string]$Path, [int]$DialogProcess = $workerId, [switch]$NativeOnly) {
        Wait-Until { [NppTestNative]::DialogFor($DialogProcess) -ne [IntPtr]::Zero } "file dialog for $Path"
        $dialog = [NppTestNative]::DialogFor($DialogProcess)
        [void][NppTestNative]::MoveWindow($dialog, -32000, -32000, 900, 600, $true)
        if ($NativeOnly) {
            Wait-Until { [NppTestNative]::FileNameEditor($dialog) -ne [IntPtr]::Zero } 'file-name control'
            Start-Sleep -Milliseconds 500
            [void][NppTestNative]::SendText([NppTestNative]::FileNameEditor($dialog), 0xC, [IntPtr]::Zero, $Path)
        } else {
            $dialogAutomation = [System.Windows.Automation.AutomationElement]::FromHandle($dialog)
            $script:dialogFileEdit = $null
            Wait-Until {
                $elements = $dialogAutomation.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
                $script:dialogFileEdit = $elements | Where-Object { $_.Current.AutomationId -eq '1148' -and $_.Current.ClassName -eq 'Edit' } | Select-Object -First 1
                $script:dialogFileEdit -ne $null
            } 'file-name editor'
            [void][NppTestNative]::SendText([IntPtr]$script:dialogFileEdit.Current.NativeWindowHandle, 0xC, [IntPtr]::Zero, $Path)
        }
        [void][NppTestNative]::PostMessage($dialog, 0x111, [IntPtr]1, [IntPtr]::Zero)
        Wait-Until { ![NppTestNative]::IsWindow($dialog) } 'file dialog acceptance'
    }
    Wait-Until { (Find-Element '5 行 | 0 条结果') -ne $null } 'source log content'
    Wait-Until { (Find-Element '搜索').Current.IsEnabled } 'ready search controls'
    $sourceRect = (Find-Element '日志原文').Current.BoundingRectangle
    $resultsRect = (Find-Element '搜索结果列表').Current.BoundingRectangle
    $queryRect = (Find-Element '搜索条件' ([System.Windows.Automation.ControlType]::Edit)).Current.BoundingRectangle
    if ($queryRect.Top -le $sourceRect.Top -or $queryRect.Bottom -gt $resultsRect.Top) { throw 'Search controls are not above the lower result pane.' }
    foreach ($name in @('刷新', '实时', '跳转行号', '跳转', '上一结果', '下一结果')) {
        $rect = (Find-Element $name).Current.BoundingRectangle
        if ($rect.IsEmpty -or [Math]::Abs(($rect.Top + $rect.Height / 2) - ($queryRect.Top + $queryRect.Height / 2)) -gt 4) { throw "Control is not in the search row: $name" }
    }
    foreach ($name in @('最近文件', '关闭文件', '返回 NPP', 'Recent', 'Close log', 'Return to NPP', '日志编码', 'Log encoding', '已打开日志', 'Open logs')) {
        if (Find-ElementName $name) { throw "Removed file control remains: $name" }
    }
    $queryEdit = Find-Element '搜索条件' ([System.Windows.Automation.ControlType]::Edit)
    if (!$queryEdit) { throw 'Query edit is missing.' }
    $queryEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('error')
    Wait-Until { $queryEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq 'error' } 'query input'
    Invoke-Control '搜索'
    Wait-Until { (Find-Element '5 行 | 2 条结果') -ne $null -and (Find-Element '搜索').Current.IsEnabled } 'GUI search'
    $live = Find-Element '实时'
    $live.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    [IO.File]::AppendAllText($log, "`nerror appended`n", [Text.UTF8Encoding]::new($false))
    Wait-Until { (Find-Element '6 行 | 3 条结果') -ne $null -and (Find-Element '搜索').Current.IsEnabled } 'live append and incremental search'
    $live.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    $lineEdit = Find-Element '跳转行号'
    Invoke-Control '下一结果'
    Wait-Until { $lineEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '5' } 'next result icon'
    Invoke-Control '上一结果'
    Wait-Until { $lineEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '2' } 'previous result icon'
    $lineEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('4')
    Wait-Until { $lineEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '4' } 'line input'
    Invoke-Control '跳转'
    Wait-Until { $lineEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '4' } 'line jump'
    Capture-Window $viewer (Join-Path $evidence 'viewer-search.png')
    Capture-Window $hostWindow (Join-Path $evidence 'npp-search.png')
    if ($Dark) { Capture-Window $hostWindow (Join-Path $evidence 'npp-dark.png') }
    $returnCommand = [NppTestNative]::MenuCommand($menu, 'Return to Notepad++')
    [void][NppTestNative]::SendMessage($hostWindow, 0x111, [IntPtr]$returnCommand, [IntPtr]::Zero)
    if ([NppTestNative]::IsWindowVisible($viewer)) { throw 'Returning to the editor failed.' }
    if (![NppTestNative]::HasVisibleEditor($hostWindow)) { throw 'The Notepad++ editor was not restored.' }
    if ([NppTestNative]::SendMessage($originalEditor, 2006, [IntPtr]::Zero, [IntPtr]::Zero).ToInt64() -ne ($originalLength + $note.Length)) { throw 'Unsaved NPP edits were changed by the plugin.' }
    if (!$AutoLog) { [void][NppTestNative]::SendMessage($originalEditor, 2176, [IntPtr]::Zero, [IntPtr]::Zero) }
    $toggle = [NppTestNative]::MenuCommand($menu, 'Toggle LogRAM workspace')
    [void][NppTestNative]::SendMessage($hostWindow, 0x111, [IntPtr]$toggle, [IntPtr]::Zero)
    Wait-Until { [NppTestNative]::IsWindowVisible($viewer) } 'restore LogRAM workspace'
    if ([NppTestNative]::HasVisibleEditor($hostWindow)) { throw 'The original editor reappeared in LogRAM mode.' }
    $newDocument = 41001
    [void][NppTestNative]::SendMessage($hostWindow, 0x111, [IntPtr]$newDocument, [IntPtr]::Zero)
    Wait-Until { ![NppTestNative]::IsWindowVisible($viewer) -and [NppTestNative]::HasVisibleEditor($hostWindow) } 'new NPP document restores editor'
    $closeDocument = 41003
    [void][NppTestNative]::SendMessage($hostWindow, 0x111, [IntPtr]$closeDocument, [IntPtr]::Zero)
    if (!$AutoLog) { [void][NppTestNative]::SendMessage($hostWindow, 0x111, [IntPtr]$toggle, [IntPtr]::Zero) }
    Wait-Until { [NppTestNative]::IsWindowVisible($viewer) -and (Find-Element '6 行 | 3 条结果') -ne $null } 'cached workspace after NPP document switch'
    $secondLog = Join-Path $evidence 'second.log'
    [IO.File]::WriteAllText($secondLog, "ERROR kept`nwarning retry`ninfo`n", [Text.UTF8Encoding]::new($false))
    $openLog = [NppTestNative]::MenuCommand($menu, 'Open log...')
    [void][NppTestNative]::SendMessage($hostWindow, 0x111, [IntPtr]$openLog, [IntPtr]::Zero)
    Set-DialogPath $secondLog $hostProcess.Id
    Wait-Until { (Find-Element '3 行 | 0 条结果') -ne $null -and (Find-Element '搜索').Current.IsEnabled } 'second log'
    $queryEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('in(error,warning);notin(retry)')
    Invoke-Control '搜索'
    Wait-Until { (Find-Element '3 行 | 1 条结果') -ne $null -and (Find-Element '搜索').Current.IsEnabled } 'advanced query in second log'
    [void][NppTestNative]::SendMessage($hostWindow, 0x111, [IntPtr]$closeDocument, [IntPtr]::Zero)
    [void][NppTestNative]::SendMessage($hostWindow, 0x111, [IntPtr]$openCurrent, [IntPtr]::Zero)
    Wait-Until { (Find-Element '6 行 | 3 条结果') -ne $null -and (Find-Element '搜索').Current.IsEnabled } 'first log query and result state'
    $queryEdit = Find-Element '搜索条件' ([System.Windows.Automation.ControlType]::Edit)
    Wait-Until { $queryEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq 'error' } 'restored first log query text'
    $exportPath = Join-Path $evidence ('results-' + [Guid]::NewGuid().ToString('N') + '.txt')
    # InvokePattern waits for a modal click handler to finish; drive its dialog from this process.
    $exportJob = Start-Job -ArgumentList @($viewer.ToInt64(), (Find-Element '导出').Current.Name) -ScriptBlock {
        param([long]$Window, [string]$Name)
        Add-Type -AssemblyName UIAutomationClient
        $form = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$Window)
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
        $button = $form.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    try {
        Set-DialogPath $exportPath -NativeOnly
        if (!(Wait-Job -Job $exportJob -Timeout 15)) { throw 'GUI export click did not return.' }
        Receive-Job -Job $exportJob -ErrorAction Stop | Out-Null
    } finally {
        if ($exportJob.State -eq 'Running') {
            $exportDialog = [NppTestNative]::DialogFor($workerId)
            if ($exportDialog -ne [IntPtr]::Zero) { [void][NppTestNative]::PostMessage($exportDialog, 0x111, [IntPtr]2, [IntPtr]::Zero) }
        }
        Stop-Job -Job $exportJob; Remove-Job -Job $exportJob
    }
    Wait-Until { (Find-Element '导出完成') -ne $null } 'GUI export'
    if ([IO.File]::ReadAllLines($exportPath).Count -ne 3) { throw 'GUI export did not include all matching lines.' }
    $gbkLog = Join-Path $evidence '编码 GBK.log'
    $gbkText = "中文错误`nnormal`n"
    [IO.File]::WriteAllText($gbkLog, $gbkText, [Text.Encoding]::GetEncoding(936))
    $gbkHash = (Get-FileHash -LiteralPath $gbkLog).Hash
    [void][NppTestNative]::PostMessage($hostWindow, 0x111, [IntPtr]41002, [IntPtr]::Zero)
    Set-DialogPath $gbkLog $hostProcess.Id
    Wait-Until { (Find-Element '2 行 | 0 条结果') -ne $null -and (Find-Element '搜索').Current.IsEnabled } 'GBK log through NPP open'
    [void][NppTestNative]::SendMessage($hostWindow, 0x111, [IntPtr]45061, [IntPtr]::Zero)
    Wait-Until { (Find-ElementName '编码 GBK.log | Gbk | 0.0 MB') -ne $null -and (Find-Element '搜索').Current.IsEnabled } 'NPP GB2312 menu controls viewer decoding'
    $queryEdit = Find-Element '搜索条件' ([System.Windows.Automation.ControlType]::Edit)
    $queryEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('中文错误')
    Invoke-Control '搜索'
    Wait-Until { (Find-Element '2 行 | 1 条结果') -ne $null -and (Find-Element '搜索').Current.IsEnabled } 'GBK Chinese search'
    [void][NppTestNative]::SendMessage($hostWindow, 0x111, [IntPtr]45008, [IntPtr]::Zero)
    Wait-Until { (Find-ElementName '编码 GBK.log | Utf8 | 0.0 MB') -ne $null -and (Find-Element '搜索').Current.IsEnabled } 'NPP UTF-8 menu controls viewer decoding'
    Invoke-Control '搜索'
    Wait-Until { (Find-Element '2 行 | 0 条结果') -ne $null -and (Find-Element '搜索').Current.IsEnabled } 'UTF-8 interpretation of GBK bytes'
    if ((Get-FileHash -LiteralPath $gbkLog).Hash -ne $gbkHash) { throw 'Encoding change modified source bytes.' }
    $encodingEditor = [NppTestNative]::ReadOnlyEditor($hostWindow)
    if ([NppTestNative]::SendMessage($encodingEditor, 2159, [IntPtr]::Zero, [IntPtr]::Zero).ToInt64() -ne 0) { throw 'Encoding change dirtied the NPP placeholder.' }
    [void][NppTestNative]::SendMessage($hostWindow, 0x111, [IntPtr]41003, [IntPtr]::Zero)
    if (!$AutoLog) { [void][NppTestNative]::SendMessage($hostWindow, 0x111, [IntPtr]$openCurrent, [IntPtr]::Zero) }
    Wait-Until { (Find-Element '6 行 | 3 条结果') -ne $null -and (Find-Element '搜索').Current.IsEnabled } 'close encoding test restores NPP tab cache'
    $queryEdit = Find-Element '搜索条件' ([System.Windows.Automation.ControlType]::Edit)
    Write-Host 'PASS: NPP encoding menus, Chinese GBK search, unchanged source and clean placeholder.'
    [void][NppTestNative]::MoveWindow($hostWindow, -32000, -32000, 1000, 800, $true)
    Wait-Until {
        $viewerRect = New-Object NppTestNative+Rect
        $panelRect = New-Object NppTestNative+Rect
        [void][NppTestNative]::GetClientRect($viewer, [ref]$viewerRect)
        [void][NppTestNative]::GetClientRect($panel, [ref]$panelRect)
        $viewerRect.Right -eq $panelRect.Right -and $viewerRect.Bottom -eq $panelRect.Bottom -and $viewerRect.Right -lt 1100
    } 'embedded viewer resize'
    [void][NppTestNative]::MoveWindow($hostWindow, -32000, -32000, 1200, 1000, $true)
    if ($LargeLog) {
        $editorLengthBeforeLargeLog = [NppTestNative]::SendMessage($originalEditor, 2006, [IntPtr]::Zero, [IntPtr]::Zero).ToInt64()
        $openLog = [NppTestNative]::MenuCommand($menu, 'Open log...')
        if ($AutoLog) {
            [void][NppTestNative]::PostMessage($hostWindow, 0x111, [IntPtr]41002, [IntPtr]::Zero)
            Set-DialogPath $LargeLog $hostProcess.Id
        } else {
            [void][NppTestNative]::SendMessage($hostWindow, 0x111, [IntPtr]$openLog, [IntPtr]::Zero)
            Set-DialogPath $LargeLog $hostProcess.Id
        }
        Wait-Until {
            $all = $automation.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
            @($all | Where-Object { $_.Current.Name.StartsWith([IO.Path]::GetFileName($LargeLog) + ' |') }).Count -gt 0
        } 'large log loading'
        if ($ExpectedLines) {
            $countName = '{0:N0} 行 | 0 条结果' -f $ExpectedLines
            if (!(Find-Element $countName)) { throw "Incorrect large-log line count; expected $ExpectedLines." }
        }
        $editorLengthAfterLargeLog = [NppTestNative]::SendMessage($originalEditor, 2006, [IntPtr]::Zero, [IntPtr]::Zero).ToInt64()
        if ($editorLengthAfterLargeLog -ne $editorLengthBeforeLargeLog) { throw 'The large log changed the standard Notepad++ editor buffer.' }
        $hostProcess.Refresh()
        $worker.Refresh()
        Write-Host ('Large-log loading proof: standard editor stays at {0} bytes; NPP working set {1:N1} MiB; LogRAM worker {2:N1} MiB.' -f $editorLengthAfterLargeLog, ($hostProcess.WorkingSet64 / 1MB), ($worker.WorkingSet64 / 1MB))
        $queryEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('error')
        Invoke-Control '搜索'
        Wait-Until { (Find-Element '搜索').Current.IsEnabled } 'large-log search completion'
        if ($ExpectedLines -and $ExpectedMatches -ge 0) {
            $countName = '{0:N0} 行 | {1:N0} 条结果' -f $ExpectedLines, $ExpectedMatches
            Wait-Until { (Find-Element $countName) -ne $null } 'large-log search results'
        }
        Capture-Window $hostWindow (Join-Path $evidence 'npp-large-log.png')
        $regex = Find-Element '正则'
        $regex.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        $queryEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('error|SendOrder')
        Invoke-Control '搜索'
        Wait-Until { (Find-Element '取消').Current.IsEnabled } 'cancellable regex search'
        Invoke-Control '取消'
        Wait-Until { (Find-Element '搜索').Current.IsEnabled } 'automatic large-log query cancelled'
        [void][NppTestNative]::PostMessage($hostWindow, 0x111, [IntPtr]41003, [IntPtr]::Zero)
        if (!$AutoLog) { [void][NppTestNative]::PostMessage($hostWindow, 0x111, [IntPtr]$openCurrent, [IntPtr]::Zero) }
        Wait-Until { (Find-Element '6 行 | 3 条结果') -ne $null -and (Find-Element '搜索').Current.IsEnabled } 'queued file opens after GUI search cancellation'
        if ($queryEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'error') { throw 'The queued file did not restore its query.' }
        $entryPoint = 'standard NPP File > Open'
        Write-Host "PASS: large log opened through $entryPoint : $LargeLog"
    }
    Write-Host "Loaded plugin and embedded viewer: NPP PID $($hostProcess.Id), worker PID $workerId, HWND $viewer"
    $release = [NppTestNative]::MenuCommand($menu, 'Close all logs / release memory')
    [void][NppTestNative]::SendMessage($hostWindow, 0x111, [IntPtr]$release, [IntPtr]::Zero)
    Wait-Until { (Find-Element '0 行 | 0 条结果') -ne $null } 'release cached logs'
    $oldWorkerId = $workerId
    $restart = [NppTestNative]::MenuCommand($menu, 'Restart LogRAM worker')
    [void][NppTestNative]::SendMessage($hostWindow, 0x111, [IntPtr]$restart, [IntPtr]::Zero)
    Wait-Until { !(Get-Process -Id $oldWorkerId -ErrorAction SilentlyContinue) } 'old worker exits on restart'
    Wait-Until { [NppTestNative]::ChildNamed($hostWindow, 'LogRAM') -ne [IntPtr]::Zero } 'restarted embedded viewer'
    $viewer = [NppTestNative]::ChildNamed($hostWindow, 'LogRAM')
    [void][NppTestNative]::GetWindowThreadProcessId($viewer, [ref]$workerId)
    if ($workerId -eq $oldWorkerId) { throw 'Restart did not replace the worker.' }
    $automation = [System.Windows.Automation.AutomationElement]::FromHandle($viewer)
    [void][NppTestNative]::SendMessage($hostWindow, 0x111, [IntPtr]$openCurrent, [IntPtr]::Zero)
    Wait-Until { (Find-Element '6 行 | 0 条结果') -ne $null -and (Find-Element '搜索').Current.IsEnabled } 'restarted worker opens saved log'
    [void][NppTestNative]::PostMessage($hostWindow, 0x10, [IntPtr]::Zero, [IntPtr]::Zero)
    Wait-Until { $hostProcess.Refresh(); $hostProcess.HasExited } 'Notepad++ shutdown'
    Wait-Until { !(Get-Process -Id $workerId -ErrorAction SilentlyContinue) } 'worker shutdown'
    $savedConfig = [IO.File]::ReadAllText($config)
    Write-Host 'PASS: real NPP load, Unicode, search/live/jump, document switch, multi-file state, export, resize, memory release, worker restart and shutdown.'
} catch {
    if ($automation) {
        $diagnostic = $automation.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        Write-Host (($diagnostic | Where-Object { $_.Current.Name -match '行 \||匹配|搜索' } | Select-Object @{n='Name';e={$_.Current.Name}}, @{n='Enabled';e={$_.Current.IsEnabled}} | Format-Table | Out-String))
    }
    Write-Host ([NppTestNative]::Dump($hostProcess.Id))
    if ($workerId) { Write-Host "Worker PID $workerId"; Write-Host ([NppTestNative]::Dump($workerId)) }
    throw
} finally {
    if (!$hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue }
    if ($workerId) { Stop-Process -Id $workerId -Force -ErrorAction SilentlyContinue }
}
