param(
    [Parameter(Mandatory)]
    [string]$PayloadPath,
    [Parameter(Mandatory)]
    [string]$IconPath
)

$ErrorActionPreference = 'Stop'
$PayloadPath = (Resolve-Path -LiteralPath $PayloadPath).Path
$IconPath = (Resolve-Path -LiteralPath $IconPath).Path
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) {
    throw 'Visual C++ build tools were not found.'
}

$devCmd = Join-Path $vsPath 'Common7\Tools\VsDevCmd.bat'
$root = Split-Path $PSScriptRoot -Parent
$workDirectory = Join-Path $root 'LogRAM\obj\single-exe-launcher'
New-Item -ItemType Directory -Force -Path $workDirectory | Out-Null

$payloadCopy = Join-Path $workDirectory 'LogRAM.payload.exe'
$resourceScript = Join-Path $workDirectory 'LogRAM-launcher.rc'
$resourceFile = Join-Path $workDirectory 'LogRAM-launcher.res'
$objectFile = Join-Path $workDirectory 'LogRAM-launcher.obj'
$launcherFile = Join-Path $workDirectory 'LogRAM.launcher.exe'
$finalPath = Join-Path (Split-Path $PayloadPath -Parent) 'LogRAM-win-x64.exe'
$sourceFile = Join-Path $root 'LogRAM-launcher\LogRAMLauncher.cpp'
Copy-Item -LiteralPath $PayloadPath -Destination $payloadCopy -Force

$resourcePayloadPath = $payloadCopy -replace '\\', '/'
$resourceIconPath = $IconPath -replace '\\', '/'
$resourceText = '1 RCDATA "' + $resourcePayloadPath + '"' + [Environment]::NewLine + '1 ICON "' + $resourceIconPath + '"' + [Environment]::NewLine
[System.IO.File]::WriteAllText($resourceScript, $resourceText, [System.Text.Encoding]::ASCII)

$command = "call `"$devCmd`" -no_logo -arch=x64 >nul && rc.exe /nologo /fo `"$resourceFile`" `"$resourceScript`" && cl.exe /nologo /O2 /MT /utf-8 /std:c++17 /DUNICODE /D_UNICODE /EHsc /c /Fo`"$objectFile`" `"$sourceFile`" && link.exe /nologo /SUBSYSTEM:WINDOWS /OUT:`"$launcherFile`" `"$objectFile`" `"$resourceFile`" user32.lib shell32.lib"
& $env:ComSpec /d /s /c $command
if ($LASTEXITCODE -ne 0) {
    throw "Single-exe launcher build failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath $launcherFile -Destination $finalPath -Force
Remove-Item -LiteralPath $PayloadPath -Force
