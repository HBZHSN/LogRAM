param(
    [string]$SourcePath = (Join-Path $PSScriptRoot '..\LogRAM-mcp\bin\Release\net8.0-windows\win-x64\publish\LogRAM-mcp.exe'),
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\LogRAM'),
    [switch]$ConfigureCodex
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourcePath).Path
$target = Join-Path $InstallDirectory 'LogRAM-mcp.exe'
New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
Copy-Item -LiteralPath $source -Destination $target -Force

if ($ConfigureCodex) {
    if (-not (Get-Command codex -ErrorAction SilentlyContinue)) {
        throw 'Codex CLI was not found. The MCP executable was installed, but Codex was not configured.'
    }

    & codex mcp add logram -- $target
    if ($LASTEXITCODE -ne 0) {
        throw "Codex configuration failed with exit code $LASTEXITCODE."
    }
}

Write-Host "Installed: $target"
Write-Host 'Generic MCP configuration:'
@{
    mcpServers = @{
        logram = @{ command = $target }
    }
} | ConvertTo-Json -Depth 4
