# Proves the release checks that read GitHub cannot mistake a failed read for an answer, by
# driving the real pack-release.ps1 against a stub gh on PATH: a release without its feed is
# drift, while a feed that will not download, a chain listing that fails and a delta-base
# listing that fails are infrastructure or a refused build, never a silent skip.
$ErrorActionPreference = "Stop"

$releaseScript = Join-Path $PSScriptRoot "pack-release.ps1"
if (-not (Test-Path $releaseScript)) { throw "pack-release.ps1 is missing." }

$onWindows = [System.IO.Path]::DirectorySeparatorChar -eq '\'
$shimDir = Join-Path ([System.IO.Path]::GetTempPath()) ("kawaiivrc-gh-stub-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $shimDir -Force | Out-Null

function Write-Stub([string]$Path, [string[]]$Lines, [string]$NewLine) {
    ($Lines -join $NewLine) | Set-Content -Path $Path -Encoding Ascii

    if (-not $Path.EndsWith(".cmd", [System.StringComparison]::OrdinalIgnoreCase)) {
        # Set the bit through .NET rather than trusting an external chmod.
        $mode = [System.IO.UnixFileMode]'UserRead, UserWrite, UserExecute, GroupRead, GroupExecute, OtherRead, OtherExecute'
        [System.IO.File]::SetUnixFileMode($Path, $mode)
    }
}

# Line endings are explicit: cmd.exe mis-parses these blocks with LF, and sh will not run a
# script whose first line ends in CRLF. exit /b inside nested if blocks also reports 0 from
# cmd.exe, so the batch halves use flat labels.
function Write-GhStub([string]$Directory, [bool]$Windows) {
    if ($Windows) {
        Write-Stub -Path (Join-Path $Directory "gh.cmd") -NewLine "`r`n" -Lines @(
            '@echo off',
            'if /I "%1 %2"=="release list" goto release_list',
            'if /I "%1 %2"=="release view" goto release_view',
            'if /I "%1 %2"=="release download" goto release_download',
            'goto done',
            '',
            ':release_list',
            'if "%GH_MODE%"=="fetch-list-fails" goto fail',
            'if not "%GH_MODE%"=="chain-list-fails" goto release_list_ok',
            'if not exist "%GH_STATE%" goto release_list_first',
            'goto fail',
            '',
            ':release_list_first',
            'type nul > "%GH_STATE%"',
            'exit /b 0',
            '',
            ':release_list_ok',
            'echo v1.0.0',
            'exit /b 0',
            '',
            ':release_view',
            'echo KawaiiVrc-1.0.0-full.nupkg',
            'if not "%GH_MODE%"=="missing-feed" echo releases.win.json',
            'exit /b 0',
            '',
            ':release_download',
            'echo download failed: connection reset 1>&2',
            'exit /b 1',
            '',
            ':fail',
            'echo gh: could not reach api.github.com 1>&2',
            'exit /b 1',
            '',
            ':done',
            'exit /b 0'
        )
        return
    }

    Write-Stub -Path (Join-Path $Directory "gh") -NewLine "`n" -Lines @(
        '#!/bin/sh',
        'case "$1 $2" in',
        '  "release list")',
        '    if [ "$GH_MODE" = "fetch-list-fails" ]; then echo "gh: could not reach api.github.com" >&2; exit 1; fi',
        '    if [ "$GH_MODE" = "chain-list-fails" ]; then',
        '      if [ -e "$GH_STATE" ]; then echo "gh: could not reach api.github.com" >&2; exit 1; fi',
        '      : > "$GH_STATE"',
        '      exit 0',
        '    fi',
        '    echo v1.0.0',
        '    exit 0 ;;',
        '  "release view")',
        '    echo KawaiiVrc-1.0.0-full.nupkg',
        '    if [ "$GH_MODE" != "missing-feed" ]; then echo releases.win.json; fi',
        '    exit 0 ;;',
        '  "release download") echo "download failed: connection reset" >&2; exit 1 ;;',
        'esac',
        'exit 0'
    )
}

# pack-release.ps1 only needs vpk to exist before the fetch, since the fetch fails first.
function Write-VpkStub([string]$Directory, [bool]$Windows) {
    if ($Windows) {
        Write-Stub -Path (Join-Path $Directory "vpk.cmd") -NewLine "`r`n" -Lines @('@echo off', 'exit /b 0')
    }
    else {
        Write-Stub -Path (Join-Path $Directory "vpk") -NewLine "`n" -Lines @('#!/bin/sh', 'exit 0')
    }
}

$verifyArguments = @('-VerifyOnly', '-Version', '1.0.0', '-Repo', 'owner/repo')

# Markers come from the plain messages, not the rendered error record: PowerShell wraps that
# record with a gutter that ends up inside the text on Linux.
$checks = @(
    @{ Name = "feed asset missing"; Mode = "missing-feed"; Expected = 1; Marker = "has no feed asset"; Arguments = $verifyArguments },
    @{ Name = "listed feed will not download"; Mode = "feed-download-fails"; Expected = 3; Marker = "Could not download the feed"; Arguments = $verifyArguments },
    @{ Name = "chain listing fails"; Mode = "chain-list-fails"; Expected = 3; Marker = "Could not list releases for the chain check"; Arguments = $verifyArguments },
    @{ Name = "delta-base listing fails"; Mode = "fetch-list-fails"; Expected = 1; Marker = "refusing to build a release without its delta base"; Arguments = @('-Version', '1.0.0', '-Repo', 'owner/repo', '-SkipPublish', '-SkipUpload', '-SkipVerify') }
)

$savedPath = $env:PATH
$savedMode = $env:GH_MODE
$savedState = $env:GH_STATE
$failures = 0

try {
    Write-GhStub -Directory $shimDir -Windows $onWindows
    Write-VpkStub -Directory $shimDir -Windows $onWindows
    $env:PATH = "$shimDir$([System.IO.Path]::PathSeparator)$savedPath"
    $env:GH_STATE = Join-Path $shimDir "list-state"

    # If the stub is not what runs, the checks would silently exercise the real GitHub.
    $stubPath = Join-Path $shimDir $(if ($onWindows) { "gh.cmd" } else { "gh" })
    $resolved = (Get-Command gh -ErrorAction SilentlyContinue).Source
    if ($resolved -ne $stubPath) { throw "gh resolves to '$resolved' instead of the stub at '$stubPath'." }

    $shell = if ($PSVersionTable.PSEdition -eq 'Core') { 'pwsh' } else { 'powershell' }
    $fileArguments = if ($onWindows) {
        @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $releaseScript)
    }
    else {
        @('-NoProfile', '-File', $releaseScript)
    }

    foreach ($check in $checks) {
        $env:GH_MODE = $check.Mode
        Remove-Item $env:GH_STATE -ErrorAction SilentlyContinue

        # The failing cases write to stderr, which Windows PowerShell turns into a
        # terminating error when 2>&1 meets ErrorActionPreference = Stop.
        $previous = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            $output = @(& $shell @($fileArguments + $check.Arguments) 2>&1)
            $actual = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previous
        }

        if ($actual -ne $check.Expected) {
            Write-Host ("{0}: exited {1}, expected {2}. Output was:" -f $check.Name, $actual, $check.Expected)
            $output | ForEach-Object { Write-Host "    $_" }
            $failures++
            continue
        }

        # PowerShell wraps long error text, inserting line breaks mid-sentence, so compare
        # against a whitespace-collapsed copy without colour codes.
        $text = (($output -join "`n") -replace '\x1b\[[0-9;]*m', '') -replace '\s+', ' '
        if ($text -notlike "*$($check.Marker)*") {
            Write-Host ("{0}: output did not contain '{1}'. Output was:" -f $check.Name, $check.Marker)
            $output | ForEach-Object { Write-Host "    $_" }
            $failures++
        }
    }
}
finally {
    $env:PATH = $savedPath
    $env:GH_MODE = $savedMode
    $env:GH_STATE = $savedState
    Remove-Item $shimDir -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures -gt 0) {
    Write-Host "$failures of $($checks.Count) feed-parity check(s) failed."
    exit 1
}

Write-Host "$($checks.Count) feed-parity check(s) passed."
