#Requires -Version 5.1
<#
.SYNOPSIS
    Build script for LayoutFixer.

.DESCRIPTION
    Builds all three release variants into .\publish\:
      - LayoutFixer-<ver>-win-x64       self-contained single-file (~73 MB)
      - LayoutFixer-<ver>-framework     framework-dependent single-file (~1 MB)
      - LayoutFixer-<ver>-source        clean source snapshot

    The version is read automatically from src\LayoutFixer\LayoutFixer.csproj
    (<Version> element), so you only bump it in one place.

.PARAMETER Variant
    Build only one variant: win-x64 | framework | source | all (default: all).

.PARAMETER Dotnet
    Path to dotnet.exe. Defaults to .\.dotnet\dotnet.exe if present, otherwise
    to "dotnet" from PATH.

.PARAMETER SkipTests
    Do not run tests\LayoutFixer.Tests before publishing (they run by default).

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Variant win-x64
#>
[CmdletBinding()]
param(
    [ValidateSet('all', 'win-x64', 'framework', 'source')]
    [string]$Variant = 'all',
    [string]$Dotnet,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $RepoRoot

# Resolve dotnet executable.
if (-not $Dotnet) {
    $localDotnet = Join-Path $RepoRoot '.dotnet\dotnet.exe'
    if (Test-Path $localDotnet) { $Dotnet = $localDotnet } else { $Dotnet = 'dotnet' }
}
Write-Host "Using dotnet: $Dotnet" -ForegroundColor Cyan

# Read version from csproj.
$csproj = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot 'src\LayoutFixer\LayoutFixer.csproj')
if ($csproj -notmatch '<Version>\s*([^<\s]+)\s*</Version>') {
    throw "Could not find <Version> in src\LayoutFixer\LayoutFixer.csproj"
}
$Version = $Matches[1]
Write-Host "LayoutFixer version: $Version" -ForegroundColor Cyan
Write-Host ''

$project = Join-Path $RepoRoot 'src\LayoutFixer\LayoutFixer.csproj'
$tests = Join-Path $RepoRoot 'tests\LayoutFixer.Tests\LayoutFixer.Tests.csproj'
$publisher = Join-Path $RepoRoot 'publish'

function Invoke-Tests {
    if ($SkipTests -or -not (Test-Path $tests)) { return }
    Write-Host "[tests] $tests" -ForegroundColor Yellow
    & $Dotnet test $tests -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "tests failed" }
}

function Build-Winx64 {
    $out = Join-Path $publisher "LayoutFixer-$Version-win-x64"
    Write-Host "[win-x64] self-contained single-file -> $out" -ForegroundColor Yellow
    & $Dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $out --nologo
    if ($LASTEXITCODE -ne 0) { throw "win-x64 publish failed" }
}

function Build-Framework {
    $out = Join-Path $publisher "LayoutFixer-$Version-framework"
    Write-Host "[framework] framework-dependent single-file -> $out" -ForegroundColor Yellow
    & $Dotnet publish $project -c Release --self-contained false `
        -p:PublishSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $out --nologo
    if ($LASTEXITCODE -ne 0) { throw "framework publish failed" }
}

function Build-Source {
    $out = Join-Path $publisher "LayoutFixer-$Version-source"
    Write-Host "[source] clean snapshot -> $out" -ForegroundColor Yellow
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $out 'src') | Out-Null

    # Root files used by the build, the docs, or branding.
    $rootFiles = @(
        '.dotnet-install.ps1', '.editorconfig', '.gitignore',
        'CHANGELOG.md', 'LayoutFixer.sln',
        'Logo1.png', 'Logo2.png',
        'README.md', 'README.en.md', 'LICENSE', 'NOTICE.md',
        'layout-switch-sound1.wav', 'layout-switch-sound2.wav'
    )
    foreach ($f in $rootFiles) {
        $src = Join-Path $RepoRoot $f
        if (Test-Path $src) { Copy-Item $src -Destination $out }
    }

    # Sources, minus build artifacts.
    $srcDir = Join-Path $out 'src\LayoutFixer'
    New-Item -ItemType Directory -Force -Path $srcDir | Out-Null
    Copy-Item -Recurse (Join-Path $RepoRoot 'src\LayoutFixer\*') $srcDir
    Remove-Item -Recurse -Force (Join-Path $srcDir 'bin') -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force (Join-Path $srcDir 'obj') -ErrorAction SilentlyContinue

    # Unit tests are part of the source (tools/ is not, see AGENTS.md #10).
    $testsSrc = Join-Path $RepoRoot 'tests\LayoutFixer.Tests'
    if (Test-Path $testsSrc) {
        $testsDir = Join-Path $out 'tests\LayoutFixer.Tests'
        New-Item -ItemType Directory -Force -Path $testsDir | Out-Null
        Copy-Item -Recurse (Join-Path $testsSrc '*') $testsDir
        Remove-Item -Recurse -Force (Join-Path $testsDir 'bin') -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force (Join-Path $testsDir 'obj') -ErrorAction SilentlyContinue
    }

    # Documentation and project memory.
    foreach ($f in @('AGENTS.md', 'build.ps1')) {
        $src = Join-Path $RepoRoot $f
        if (Test-Path $src) { Copy-Item $src -Destination $out }
    }
    $docs = Join-Path $RepoRoot 'docs'
    if (Test-Path $docs) { Copy-Item -Recurse $docs (Join-Path $out 'docs') }
}

if ($Variant -ne 'source') { Invoke-Tests }

switch ($Variant) {
    'all'       { Build-Winx64; Build-Framework; Build-Source }
    'win-x64'   { Build-Winx64 }
    'framework' { Build-Framework }
    'source'    { Build-Source }
}

Write-Host ''
Write-Host "Done. Artifacts in: $publisher" -ForegroundColor Green
