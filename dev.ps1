<#
.SYNOPSIS
    Lets ./dev work from PowerShell too, by running the bash script beside it.

.DESCRIPTION
    The real script is ./dev, a bash script. PowerShell does not run a file
    without an extension itself: it hands it to Windows to "open", which pops
    up a new window instead of running anything. With this file next to it,
    PowerShell picks dev.ps1 for ./dev, and this runs ./dev through Git Bash,
    so the commands work the same in either shell:

        ./dev up | down | restart | status | logs [service] | reset [--force]

    Git Bash is used rather than the first bash on PATH, which on Windows is
    usually WSL's and may not see Docker Desktop.
#>

$ErrorActionPreference = 'Stop'

# Git for Windows keeps bash.exe in its bin folder; git.exe on PATH says where
# Git is installed, whichever of its folders PATH happens to name.
$candidates = @()
$git = Get-Command git -ErrorAction SilentlyContinue
if ($git) {
    $folder = Split-Path $git.Source
    for ($i = 0; $i -lt 3 -and $folder; $i++) {
        $candidates += Join-Path $folder 'bin\bash.exe'
        $folder = Split-Path $folder
    }
}
$candidates += Join-Path $env:ProgramFiles 'Git\bin\bash.exe'

$bash = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $bash) {
    throw 'Git Bash was not found. Install Git for Windows, or run ./dev from Git Bash or WSL.'
}

# Forward slashes, which bash takes as a path without any escaping.
$script = (Join-Path $PSScriptRoot 'dev') -replace '\\', '/'

# Docker reports progress on stderr. Windows PowerShell 5.1 would turn that
# into error records if the output is redirected, so judge by the exit code.
$ErrorActionPreference = 'Continue'
& $bash $script @args
exit $LASTEXITCODE
