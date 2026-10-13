param(
    [Parameter(Mandatory)] [string]$NotepadDirectory,
    [ValidateSet('x64', 'x86', 'arm64')] [string]$Architecture = 'x64'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repo "LogRAM.Npp\bin\package-$Architecture\LogRAM.Npp"
$notepad = Join-Path $NotepadDirectory 'notepad++.exe'
if (!(Test-Path -LiteralPath $notepad)) { throw "Notepad++ was not found: $notepad" }
if (!(Test-Path -LiteralPath (Join-Path $source 'LogRAM.Npp.dll'))) { throw 'Build the plugin package first with scripts/build-npp.ps1.' }
function Get-PeMachine([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Path).Path)
    if ($bytes.Length -lt 64 -or [BitConverter]::ToUInt16($bytes, 0) -ne 0x5a4d) { throw "Invalid Windows binary: $Path" }
    $offset = [BitConverter]::ToInt32($bytes, 0x3c)
    if ($offset -lt 0 -or $offset + 6 -gt $bytes.Length -or [BitConverter]::ToUInt32($bytes, $offset) -ne 0x4550) { throw "Invalid PE header: $Path" }
    return [BitConverter]::ToUInt16($bytes, $offset + 4)
}
if ((Get-PeMachine $notepad) -ne (Get-PeMachine (Join-Path $source 'LogRAM.Npp.dll'))) { throw 'The plugin package architecture must match Notepad++.' }
$resolvedDirectory = (Resolve-Path -LiteralPath $NotepadDirectory).Path
$resolvedExecutable = Join-Path $resolvedDirectory 'notepad++.exe'
$running = Get-Process -Name 'notepad++' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $resolvedExecutable }
if ($running) { throw 'Close Notepad++ before installing or updating the plugin.' }
$destination = Join-Path $resolvedDirectory 'plugins\LogRAM.Npp'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Get-ChildItem -LiteralPath $source | Copy-Item -Destination $destination -Recurse -Force
Write-Host "Installed: $destination"
