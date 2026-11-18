# Proves the feed-parity decisions without touching GitHub, by putting a stub gh on PATH and
# running the real pack-release.ps1: a release that does not list releases.win.json is drift,
# while a listed feed that will not download is a transport problem, not drift.
$ErrorActionPreference = "Stop"

$releaseScript = Join-Path $PSScriptRoot "pack-release.ps1"
if (-not (Test-Path $releaseScript)) { throw "pack-release.ps1 is missing." }

$onWindows = [System.IO.Path]::DirectorySeparatorChar -eq '\'
$shimDir = Join-Path ([System.IO.Path]::GetTempPath()) ("kawaiivrc-gh-stub-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $shimDir -Force | Out-Null

function Write-GhStub([string]$Directory, [bool]$Windows) {
    if ($Windows) {
        $path = Join-Path $Directory "gh.cmd"
        @'
@echo off
if /I "%1 %2"=="release list" (
  echo v1.0.0
  exit /b 0
)
if /I "%1 %2"=="release view" (
  echo KawaiiVrc-1.0.0-full.nupkg
  if "%HAS_FEED%"=="1" echo releases.win.json
  exit /b 0
)
if /I "%1 %2"=="release download" (
  echo download failed: connection reset 1>&2
  exit /b 1
)
exit /b 0
'@ | Set-Content -Path $path -Encoding Ascii
        return
    }

    $path = Join-Path $Directory "gh"
    @'
#!/bin/sh
case "$1 $2" in
  "release list") echo v1.0.0; exit 0 ;;
  "release view")
    echo KawaiiVrc-1.0.0-full.nupkg
    if [ "$HAS_FEED" = "1" ]; then echo releases.win.json; fi
    exit 0 ;;
  "release download") echo "download failed: connection reset" >&2; exit 1 ;;
esac
exit 0
'@ | Set-Content -Path $path -Encoding Ascii
    & chmod '+x' $path
    if ($LASTEXITCODE -ne 0) { throw "Could not make the gh stub executable." }
}

$savedPath = $env:PATH
$failures = 0

try {
    Write-GhStub -Directory $shimDir -Windows $onWindows
    $env:PATH = "$shimDir$([System.IO.Path]::PathSeparator)$savedPath"

    $shell = if ($PSVersionTable.PSEdition -eq 'Core') { 'pwsh' } else { 'powershell' }
    $arguments = if ($onWindows) {
        @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $releaseScript)
    }
    else {
        @('-NoProfile', '-File', $releaseScript)
    }
    $arguments += @('-VerifyOnly', '-Version', '1.0.0', '-Repo', 'owner/repo')

    $checks = @(
        @{ Name = "feed asset missing"; HasFeed = "0"; Expected = 1; Marker = "releases.win.json is missing" },
        @{ Name = "listed feed will not download"; HasFeed = "1"; Expected = 3; Marker = "not release drift" }
    )

    foreach ($check in $checks) {
        $env:HAS_FEED = $check.HasFeed

        # The failing case writes to stderr, which Windows PowerShell turns into a
        # terminating error when 2>&1 meets ErrorActionPreference = Stop.
        $previous = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            $output = @(& $shell @arguments 2>&1)
            $actual = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previous
        }

        if ($actual -ne $check.Expected) {
            Write-Host ("{0}: exited {1}, expected {2}" -f $check.Name, $actual, $check.Expected)
            $failures++
            continue
        }

        if ("$($output -join "`n")" -notlike "*$($check.Marker)*") {
            Write-Host ("{0}: output did not contain '{1}'" -f $check.Name, $check.Marker)
            $failures++
        }
    }
}
finally {
    $env:PATH = $savedPath
    Remove-Item -Path Env:\HAS_FEED -ErrorAction SilentlyContinue
    Remove-Item $shimDir -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures -gt 0) {
    Write-Host "$failures of $($checks.Count) feed-parity check(s) failed."
    exit 1
}

Write-Host "$($checks.Count) feed-parity check(s) passed."
