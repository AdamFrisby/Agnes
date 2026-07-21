#!/usr/bin/env pwsh
#
# Build distributable Agnes artifacts for the common platforms into builds/ (gitignored).
#
#   ./build.ps1                       # everything: windows, linux, mac (arm64+x64), android, web
#   ./build.ps1 linux windows         # only those desktop targets
#   ./build.ps1 android web           # only the mobile / web heads
#   ./build.ps1 -ClientOnly linux     # skip the host daemon (build just the desktop app)
#
# Output layout (builds/ is git-ignored):
#   builds/windows/Agnes.exe          + builds/windows/host/Agnes.Host.exe
#   builds/linux/Agnes                + builds/linux/host/Agnes.Host
#   builds/mac/arm64/Agnes            + builds/mac/arm64/host/Agnes.Host
#   builds/mac/x64/Agnes              + builds/mac/x64/host/Agnes.Host
#   builds/android/*.apk
#   builds/web/                       (static WebAssembly site — serve the published folder)
#
# The desktop client and host are self-contained, single-file native executables (no .NET install
# needed on the target) and are NOT trimmed (Avalonia and the host rely on reflection). Android and
# web are only built when their workloads are installed (dotnet workload install …).
#
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Targets,
    [switch] $ClientOnly
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$Root = $PSScriptRoot
$Out  = Join-Path $Root 'builds'

$DesktopProj = 'src/Agnes.App.Desktop/Agnes.App.Desktop.csproj'
$HostProj    = 'src/Agnes.Host/Agnes.Host.csproj'
$UnoProj     = 'src/Agnes.App/Agnes.App/Agnes.App.csproj'
$Config      = 'Release'
$BuildHost   = -not $ClientOnly

if (-not $Targets -or $Targets -contains 'all') {
    $Targets = @('windows', 'linux', 'mac', 'android', 'web')
}
function Want([string] $t) { $Targets -contains $t }

# Self-contained, single-file, no-trim native publish flags.
$Common = @(
    '-c', $Config, '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=none', '-p:DebugSymbols=false', '--nologo'
)

function Publish-Desktop([string] $rid, [string] $dir) {
    Write-Host "==> desktop client · $rid -> $dir"
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    dotnet publish $DesktopProj -r $rid @Common -o $dir | Out-Null
    $sfx = if ($rid -eq 'win-x64') { '.exe' } else { '' }
    $src = Join-Path $dir "Agnes.App.Desktop$sfx"
    if (Test-Path $src) { Move-Item -Force $src (Join-Path $dir "Agnes$sfx") }  # friendlier app-host name
    # Native debug symbols (e.g. Skia/HarfBuzz .pdb) aren't needed at runtime and bloat the bundle.
    Get-ChildItem -Recurse -Path $dir -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force
}

function Publish-Host([string] $rid, [string] $dir) {
    if (-not $BuildHost) { return }
    $hd = Join-Path $dir 'host'
    Write-Host "==> host daemon   · $rid -> $hd"
    if (Test-Path $hd) { Remove-Item -Recurse -Force $hd }
    New-Item -ItemType Directory -Force -Path $hd | Out-Null
    dotnet publish $HostProj -r $rid @Common -o $hd | Out-Null
    Get-ChildItem -Recurse -Path $hd -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force
}

function Desktop-Target([string] $rid, [string] $dir) {
    Publish-Desktop $rid $dir
    Publish-Host $rid $dir
}

# ---- desktop OSes ----
if (Want 'windows') { Desktop-Target 'win-x64'   (Join-Path $Out 'windows') }
if (Want 'linux')   { Desktop-Target 'linux-x64' (Join-Path $Out 'linux') }
if (Want 'mac') {
    Desktop-Target 'osx-arm64' (Join-Path $Out 'mac/arm64')
    Desktop-Target 'osx-x64'   (Join-Path $Out 'mac/x64')
}

# ---- android apk ----
if (Want 'android') {
    if ((dotnet workload list) -match '\bandroid\b') {
        Write-Host '==> android apk'
        $ad = Join-Path $Out 'android'
        if (Test-Path $ad) { Remove-Item -Recurse -Force $ad }
        New-Item -ItemType Directory -Force -Path $ad | Out-Null
        dotnet publish $UnoProj -f net10.0-android -c $Config --nologo -o (Join-Path $ad '_stage') | Out-Null
        Get-ChildItem -Recurse -Path (Join-Path $ad '_stage'), 'src/Agnes.App' -Filter *.apk -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match 'net10.0-android' -or $_.DirectoryName -like "*_stage*" } |
            ForEach-Object { Copy-Item -Force $_.FullName $ad }
        Remove-Item -Recurse -Force (Join-Path $ad '_stage') -ErrorAction SilentlyContinue
        if (-not (Get-ChildItem $ad -Filter *.apk -ErrorAction SilentlyContinue)) {
            Write-Host '   (no .apk produced — check the android SDK / signing keystore)'
        }
    }
    else { Write-Host "!! skipping android — the 'android' workload isn't installed (dotnet workload install android)" }
}

# ---- web (WebAssembly) ----
if (Want 'web') {
    if ((dotnet workload list) -match '\bwasm-tools\b') {
        Write-Host '==> web (WebAssembly)'
        $wd = Join-Path $Out 'web'
        if (Test-Path $wd) { Remove-Item -Recurse -Force $wd }
        dotnet publish $UnoProj -f net10.0-browserwasm -c $Config --nologo -o $wd | Out-Null
    }
    else { Write-Host "!! skipping web — the 'wasm-tools' workload isn't installed (dotnet workload install wasm-tools)" }
}

Write-Host "`nDone. Artifacts under builds/."
