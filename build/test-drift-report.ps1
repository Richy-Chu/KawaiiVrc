# Proves report-drift.ps1 cannot open a duplicate issue or report a false pass when a gh call
# fails, by running the real script against a stub gh on PATH. Every gh call there is now
# checked, so an unreadable issue list stops the step instead of looking like "none open".
$ErrorActionPreference = "Stop"

$driftScript = Join-Path $PSScriptRoot "report-drift.ps1"
if (-not (Test-Path $driftScript)) { throw "report-drift.ps1 is missing." }

$onWindows = [System.IO.Path]::DirectorySeparatorChar -eq '\'
$shimDir = Join-Path ([System.IO.Path]::GetTempPath()) ("kawaiivrc-drift-stub-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $shimDir -Force | Out-Null

function Write-Stub([string]$Path, [string[]]$Lines, [string]$NewLine) {
    ($Lines -join $NewLine) | Set-Content -Path $Path -Encoding Ascii

    if (-not $Path.EndsWith(".cmd", [System.StringComparison]::OrdinalIgnoreCase)) {
        # Set the bit through .NET rather than trusting an external chmod.
        $mode = [System.IO.UnixFileMode]'UserRead, UserWrite, UserExecute, GroupRead, GroupExecute, OtherRead, OtherExecute'
        [System.IO.File]::SetUnixFileMode($Path, $mode)
    }
}

# Line endings are explicit, and the batch half uses flat labels: exit /b inside nested if
# blocks reports 0 from cmd.exe.
function Write-GhStub([string]$Directory, [bool]$Windows) {
    if ($Windows) {
        Write-Stub -Path (Join-Path $Directory "gh.cmd") -NewLine "`r`n" -Lines @(
            '@echo off',
            'echo %1 %2>>"%DRIFT_LOG%"',
            'if /I "%1 %2"=="issue list" goto issue_list',
            'if /I "%1 %2"=="issue close" goto issue_close',
            'if /I "%1 %2"=="issue create" goto issue_create',
            'goto done',
            '',
            ':issue_list',
            'if "%DRIFT_MODE%"=="list-fails" goto fail',
            'if "%DRIFT_MODE%"=="none" goto done',
            'echo 7',
            'exit /b 0',
            '',
            ':issue_close',
            'if "%DRIFT_MODE%"=="close-fails" goto fail',
            'exit /b 0',
            '',
            ':issue_create',
            'echo https://example.test/issues/9',
            'exit /b 0',
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
        'echo "$1 $2" >> "$DRIFT_LOG"',
        'case "$1 $2" in',
        '  "issue list")',
        '    if [ "$DRIFT_MODE" = "list-fails" ]; then echo "gh: could not reach api.github.com" >&2; exit 1; fi',
        '    if [ "$DRIFT_MODE" = "none" ]; then exit 0; fi',
        '    echo 7',
        '    exit 0 ;;',
        '  "issue close")',
        '    if [ "$DRIFT_MODE" = "close-fails" ]; then echo "gh: could not reach api.github.com" >&2; exit 1; fi',
        '    exit 0 ;;',
        '  "issue create") echo "https://example.test/issues/9"; exit 0 ;;',
        'esac',
        'exit 0'
    )
}

$checks = @(
    @{ Name = "failure with an unreadable issue list"; Mode = "list-fails"; Outcome = "failure"; Expected = 1; Marker = "issue state is unknown"; ForbidCreate = $true },
    @{ Name = "pass with an unreadable issue list"; Mode = "list-fails"; Outcome = "success"; Expected = 1; Marker = "issue state is unknown"; ForbidPass = $true },
    @{ Name = "pass that cannot close the issue"; Mode = "close-fails"; Outcome = "success"; Expected = 1; Marker = "issue state is unknown" },
    @{ Name = "pass closes the open issue"; Mode = "ok"; Outcome = "success"; Expected = 0; Marker = "Closed issue #7" }
)

$savedPath = $env:PATH
$savedMode = $env:DRIFT_MODE
$savedLog = $env:DRIFT_LOG
$savedRepo = $env:REPO
$savedUrl = $env:RUN_URL
$savedOutcome = $env:OUTCOME
$savedKind = $env:FAILURE_KIND
$failures = 0

try {
    Write-GhStub -Directory $shimDir -Windows $onWindows
    $env:PATH = "$shimDir$([System.IO.Path]::PathSeparator)$savedPath"
    $env:REPO = "owner/repo"
    $env:RUN_URL = "https://example.test/run/1"
    $env:DRIFT_LOG = Join-Path $shimDir "calls.log"

    $shell = if ($PSVersionTable.PSEdition -eq 'Core') { 'pwsh' } else { 'powershell' }
    $fileArguments = if ($onWindows) {
        @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $driftScript)
    }
    else {
        @('-NoProfile', '-File', $driftScript)
    }

    foreach ($check in $checks) {
        $env:DRIFT_MODE = $check.Mode
        $env:OUTCOME = $check.Outcome
        Remove-Item -Path Env:\FAILURE_KIND -ErrorAction SilentlyContinue
        Remove-Item $env:DRIFT_LOG -ErrorAction SilentlyContinue

        # The failing cases write to stderr, which Windows PowerShell turns into a
        # terminating error when 2>&1 meets ErrorActionPreference = Stop.
        $previous = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            $output = @(& $shell @fileArguments 2>&1)
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
        $problem = $null

        if ($text -notlike "*$($check.Marker)*") {
            $problem = "output did not contain '$($check.Marker)'"
        }
        elseif ($check.ForbidCreate -and (Get-Content $env:DRIFT_LOG -Raw -ErrorAction SilentlyContinue) -match "issue create") {
            $problem = "it opened an issue even though the issue list could not be read"
        }
        elseif ($check.ForbidPass -and $text -like "*Verification passed and no open drift issue*") {
            $problem = "it reported a pass even though the issue list could not be read"
        }

        if ($problem) {
            Write-Host ("{0}: {1}. Output was:" -f $check.Name, $problem)
            $output | ForEach-Object { Write-Host "    $_" }
            $failures++
        }
    }
}
finally {
    $env:PATH = $savedPath
    $env:DRIFT_MODE = $savedMode
    $env:DRIFT_LOG = $savedLog
    $env:REPO = $savedRepo
    $env:RUN_URL = $savedUrl
    $env:OUTCOME = $savedOutcome
    $env:FAILURE_KIND = $savedKind
    Remove-Item $shimDir -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures -gt 0) {
    Write-Host "$failures of $($checks.Count) drift-report check(s) failed."
    exit 1
}

Write-Host "$($checks.Count) drift-report check(s) passed."
