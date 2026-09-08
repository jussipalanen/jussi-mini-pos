<#
.SYNOPSIS
    Publishes JussiMiniPos and compiles the Windows installer.

.DESCRIPTION
    Two steps that have to happen in order: a self-contained single-file
    publish, then Inno Setup over the result. The installer reads its version
    out of the published executable, so publishing first is not optional.

    Self-contained on purpose. The framework-dependent build is 2.8 MB instead
    of 136 MB, but it needs the .NET 10 Desktop Runtime on the target machine,
    which turns every install into a runtime download that can fail. Carrying
    the runtime is the cheaper trade for something handed over on a USB stick.

.PARAMETER Runtime
    Publish target. win-x64 covers x64 and, through emulation, ARM64.

.EXAMPLE
    .\installer\build.ps1
    Publishes and builds installer\Output\JussiMiniPos-<version>-setup.exe.

.EXAMPLE
    .\installer\build.ps1 -SkipPublish
    Recompiles the installer from whatever was published last.
#>
[CmdletBinding()]
param(
    [string] $Runtime = 'win-x64',
    [switch] $SkipPublish
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'JussiMiniPos.csproj'
$script = Join-Path $PSScriptRoot 'JussiMiniPos.iss'
$publishDir = Join-Path $repo "bin\Release\net10.0-windows\$Runtime\publish"

if (-not $SkipPublish) {
    Write-Host 'Publishing (self-contained, single file)...' -ForegroundColor Cyan

    dotnet publish $project `
        -c Release `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        --nologo

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
}

$exe = Join-Path $publishDir 'JussiMiniPos.exe'
if (-not (Test-Path $exe)) {
    throw "Published executable not found at $exe. Run without -SkipPublish."
}

# Inno Setup does not put itself on PATH, so look where it installs. 6.3 or
# newer is needed for ArchitecturesAllowed=x64compatible.
$iscc = (Get-Command 'iscc.exe' -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    foreach ($candidate in @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $candidate) { $iscc = $candidate; break }
    }
}

if (-not $iscc) {
    throw @'
Inno Setup was not found. Install it (free, about 5 MB) and run this again:

    winget install --id JRSoftware.InnoSetup

or download it from https://jrsoftware.org/isdl.php
'@
}

Write-Host "Compiling the installer with $iscc ..." -ForegroundColor Cyan
& $iscc $script
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }

$setup = Get-ChildItem (Join-Path $PSScriptRoot 'Output') -Filter '*-setup.exe' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

Write-Host ''
Write-Host 'Installer ready:' -ForegroundColor Green
Write-Host ("  {0}" -f $setup.FullName)
Write-Host ("  {0:N1} MB" -f ($setup.Length / 1MB))
Write-Host ''
Write-Host 'Copy it to a USB stick with, for example:' -ForegroundColor Cyan
Write-Host ("  Copy-Item '{0}' E:\ -Verbose" -f $setup.FullName)
