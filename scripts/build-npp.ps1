param(
    [ValidateSet('x64', 'x86', 'arm64')] [string]$Architecture = 'x64',
    [switch]$FrameworkDependent
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$build = Join-Path $repo "LogRAM.Npp\bin\native-$Architecture"
$variant = if ($FrameworkDependent) { '-fd' } else { '' }
$package = Join-Path $repo "LogRAM.Npp\bin\package-$Architecture$variant\LogRAM.Npp"
$version = ([xml](Get-Content -LiteralPath (Join-Path $repo 'LogRAM.Npp.Viewer\LogRAM.Npp.Viewer.csproj') -Raw)).Project.PropertyGroup.Version
$rid = "win-$Architecture"
New-Item -ItemType Directory -Force -Path $package | Out-Null
# CMake 3.29 knows VS 2022; a recent CMake also supports VS 2026. MinGW is sufficient for x64.
if ($Architecture -eq 'x64' -and (Get-Command g++ -ErrorAction SilentlyContinue)) {
    & cmake -S (Join-Path $repo 'LogRAM.Npp') -B $build -G 'MinGW Makefiles' -DCMAKE_BUILD_TYPE=Release
} else {
    $platform = @{ x64 = 'x64'; x86 = 'Win32'; arm64 = 'ARM64' }[$Architecture]
    # Visual Studio CMake generators reject # in output paths (this repository can live under c#).
    $build = Join-Path $env:LOCALAPPDATA "LogRAM\build\npp-$Architecture-msvc"
    & cmake -S (Join-Path $repo 'LogRAM.Npp') -B $build -G 'Visual Studio 17 2022' -A $platform
}
if ($LASTEXITCODE) { throw 'Native plugin configuration failed.' }
& cmake --build $build --config Release
if ($LASTEXITCODE) { throw 'Native plugin build failed.' }
$routerTest = Join-Path $build 'LogRAMNppRouterTest.exe'
if (!(Test-Path -LiteralPath $routerTest)) { $routerTest = Join-Path $build 'Release\LogRAMNppRouterTest.exe' }
& $routerTest
if ($LASTEXITCODE) { throw 'Native .log routing self-test failed.' }
$nativeDll = Join-Path $build 'LogRAM.Npp.dll'
if (!(Test-Path -LiteralPath $nativeDll)) { $nativeDll = Join-Path $build 'Release\LogRAM.Npp.dll' }
Copy-Item -LiteralPath $nativeDll -Destination $package
& dotnet publish (Join-Path $repo 'LogRAM.Npp.Viewer\LogRAM.Npp.Viewer.csproj') -c Release -r $rid --self-contained (!$FrameworkDependent) -o $package
if ($LASTEXITCODE) { throw 'Viewer publish failed.' }
Copy-Item -LiteralPath (Join-Path $repo 'LogRAM.Npp\README.md') -Destination $package
Copy-Item -LiteralPath (Join-Path $repo 'LogRAM.Npp\VALIDATION.md') -Destination $package
if (Test-Path -LiteralPath (Join-Path $repo 'LICENSE')) { Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $package }
Copy-Item -LiteralPath (Join-Path $repo 'LogRAM.Npp\licenses') -Destination $package -Recurse -Force
foreach ($check in @('--self-test', '--ui-self-test')) {
    $test = Start-Process -FilePath (Join-Path $package 'LogRAM.Npp.Viewer.exe') -ArgumentList $check -WindowStyle Hidden -Wait -PassThru
    if ($test.ExitCode) { throw "Plugin self-test failed: $check" }
}
$zip = Join-Path (Split-Path -Parent $package) "LogRAM.Npp-$version-$Architecture$variant.zip"
Compress-Archive -LiteralPath $package -DestinationPath $zip -Force
Write-Host "Plugin package: $zip"
$adminZip = Join-Path (Split-Path -Parent $package) "LogRAM.Npp-$version-$Architecture$variant-plugin-admin.zip"
Compress-Archive -LiteralPath (Get-ChildItem -LiteralPath $package).FullName -DestinationPath $adminZip -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($adminZip)
try {
    if (!$archive.GetEntry('LogRAM.Npp.dll') -or !$archive.GetEntry('LogRAM.Npp.Viewer.exe')) {
        throw 'Plugins Admin archive must contain the plugin DLL and viewer at the ZIP root.'
    }
} finally { $archive.Dispose() }
Write-Host "Plugins Admin package: $adminZip"
