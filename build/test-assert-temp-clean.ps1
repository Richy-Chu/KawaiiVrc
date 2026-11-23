# Checks the verdict of build/assert-temp-clean.ps1 without running the suite: a genuine
# Chromium deferral passes, and a folder that only looks deferred fails - no note at all, a note
# naming a locked store file, a note whose .tmp file was not empty, a note too old to still be
# waiting for a sweep. Every case is planted in a sandbox and the check is pointed at it, so the
# machine's own %TEMP% never decides one. Browser profile folders get the same treatment: a dead
# pid's profile is allowed to wait for the sweep, a name the sweep cannot read and a live pid's
# folder are not.
$ErrorActionPreference = "Stop"

$check = Join-Path $PSScriptRoot "assert-temp-clean.ps1"
$shell = (Get-Process -Id $PID).Path
$root = Join-Path ([System.IO.Path]::GetTempPath()) ("kawaiivrc-temp-check-" + [guid]::NewGuid().ToString("N"))

# No process can own this id, so a folder named for it is a finished run's.
$deadPid = 2147483000

# The note's timestamp is what the check ages, so a passing case has to be written now: a fixed
# date would be a stale deferral by the time anyone runs this.
$now = (Get-Date).ToUniversalTime().ToString("O")
$stale = (Get-Date).ToUniversalTime().AddHours(-3).ToString("O")

function Get-Sandbox {
    $sandbox = Join-Path $root ([guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
    return $sandbox
}

function Write-RunFile([string] $sandbox, [string] $name, [int] $bytes) {
    $path = Join-Path (Join-Path $sandbox ("vrc-companion-tests-" + $deadPid)) $name
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    [System.IO.File]::WriteAllBytes($path, (New-Object byte[] $bytes))
}

function Write-RunNote([string] $sandbox, [string[]] $lines) {
    $note = Join-Path (Join-Path $sandbox ("vrc-companion-tests-" + $deadPid)) "cleanup-deferred.txt"
    [System.IO.File]::WriteAllLines($note, $lines)
}

function ProfileFolder([string] $sandbox, [string] $owner) {
    # A profile is a folder of files its browser holds open, not temp files in a run folder: the
    # check must not apply the .tmp rule here, only the name and the owner.
    $folder = Join-Path $sandbox ("vrc-companion-browser-" + $owner)
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    [System.IO.File]::WriteAllBytes((Join-Path $folder "Preferences"), (New-Object byte[] 64))
    return $folder
}

function Invoke-Check([string] $sandbox) {
    $output = @(& $shell -NoProfile -File $check -TempPath $sandbox -WaitSeconds 0 2>&1)
    $code = $LASTEXITCODE
    return [pscustomobject]@{ ExitCode = $code; Text = ($output -join "`n") }
}

$checks = @(
    @{
        # A dead pid's profile is the one folder that always outlives its run, so it must pass
        # beside a genuine deferral - and its files are not judged as temp files.
        Name = "a genuine deferral and a finished browser profile pass"
        Expected = 0
        Marker = "Browser profile vrc-companion-browser-$deadPid stays until the next run sweeps it"
        Plant = {
            param($sandbox)
            Write-RunFile $sandbox "11111111-1111-1111-1111-111111111111.tmp" 0
            Write-RunFile $sandbox "22222222-2222-2222-2222-222222222222.tmp" 0
            Write-RunNote $sandbox @(
                "host pid: $deadPid",
                "utc: $now",
                "held: 11111111-1111-1111-1111-111111111111.tmp 0",
                "held: 22222222-2222-2222-2222-222222222222.tmp 0")
            ProfileFolder $sandbox $deadPid | Out-Null
        }
    },
    @{
        Name = "a folder with no note fails"
        Expected = 1
        Plant = {
            param($sandbox)
            Write-RunFile $sandbox "11111111-1111-1111-1111-111111111111.tmp" 0
        }
    },
    @{
        Name = "a note naming a locked store file fails"
        Expected = 1
        Marker = "not a .tmp"
        Plant = {
            param($sandbox)
            Write-RunFile $sandbox "11111111-1111-1111-1111-111111111111.tmp" 0
            Write-RunFile $sandbox "settings.json" 412
            Write-RunNote $sandbox @(
                "host pid: $deadPid",
                "utc: $now",
                "held: 11111111-1111-1111-1111-111111111111.tmp 0",
                "held: settings.json 412")
        }
    },
    @{
        Name = "a note whose .tmp file was not empty fails"
        Expected = 1
        Plant = {
            param($sandbox)
            Write-RunFile $sandbox "11111111-1111-1111-1111-111111111111.tmp" 8
            Write-RunNote $sandbox @(
                "host pid: $deadPid",
                "utc: $now",
                "held: 11111111-1111-1111-1111-111111111111.tmp 8")
        }
    },
    @{
        Name = "a note with nothing held in it fails"
        Expected = 1
        Marker = "lists no held file"
        Plant = {
            param($sandbox)
            New-Item -ItemType Directory -Path (Join-Path $sandbox ("vrc-companion-tests-" + $deadPid)) -Force | Out-Null
            Write-RunNote $sandbox @("host pid: $deadPid", "utc: $now")
        }
    },
    @{
        # The note is not an inventory of the folder: a file it leaves out is a file the run
        # could not delete and did not explain, which is what "locked" was supposed to mean.
        Name = "a folder holding a file the note does not name fails"
        Expected = 1
        Marker = "the folder holds 1 file(s) the note does not: settings.json (412 bytes)"
        Plant = {
            param($sandbox)
            Write-RunFile $sandbox "11111111-1111-1111-1111-111111111111.tmp" 0
            Write-RunFile $sandbox "settings.json" 412
            Write-RunNote $sandbox @(
                "host pid: $deadPid",
                "utc: $now",
                "held: 11111111-1111-1111-1111-111111111111.tmp 0")
        }
    },
    @{
        # A deferral waits for the next run's sweep. One that has waited hours means the sweep is
        # not taking them, so the folder would stand for good.
        Name = "a note too old to be waiting for a sweep fails"
        Expected = 1
        Marker = "hours old, so the next run's sweep should have taken this folder"
        Plant = {
            param($sandbox)
            Write-RunFile $sandbox "11111111-1111-1111-1111-111111111111.tmp" 0
            Write-RunNote $sandbox @(
                "host pid: $deadPid",
                "utc: $stale",
                "held: 11111111-1111-1111-1111-111111111111.tmp 0")
        }
    },
    @{
        # A name the sweep cannot read is a profile no run will ever reclaim.
        Name = "a browser profile this check cannot name fails"
        Expected = 1
        Marker = "not a browser folder this check can name"
        Plant = {
            param($sandbox)
            ProfileFolder $sandbox "not-a-pid" | Out-Null
        }
    },
    @{
        # This process is the live owner: a profile still in use is not a finished run's litter.
        Name = "a browser profile whose process is still running fails"
        Expected = 1
        Marker = "is still running"
        Plant = {
            param($sandbox)
            ProfileFolder $sandbox $PID | Out-Null
        }
    },
    @{
        # A run that could not read its folder must not read as one that found nothing in it.
        Name = "a note saying the folder could not be listed fails with that reason"
        Expected = 1
        Marker = "could not list the folder (DirectoryNotFoundException: no such path)"
        Plant = {
            param($sandbox)
            New-Item -ItemType Directory -Path (Join-Path $sandbox ("vrc-companion-tests-" + $deadPid)) -Force | Out-Null
            Write-RunNote $sandbox @(
                "host pid: $deadPid",
                "utc: $now",
                "unlisted: DirectoryNotFoundException: no such path")
        }
    }
)

$failures = 0
New-Item -ItemType Directory -Path $root -Force | Out-Null
try {
    foreach ($checkCase in $checks) {
        $sandbox = Get-Sandbox
        & $checkCase.Plant $sandbox
        $result = Invoke-Check $sandbox
        if ($result.ExitCode -ne $checkCase.Expected) {
            Write-Host ("{0}: exited {1}, expected {2}" -f $checkCase.Name, $result.ExitCode, $checkCase.Expected)
            Write-Host $result.Text
            $failures++
            continue
        }

        if ($checkCase.Marker -and ($result.Text -notlike "*$($checkCase.Marker)*")) {
            Write-Host ("{0}: output did not say '{1}'" -f $checkCase.Name, $checkCase.Marker)
            Write-Host $result.Text
            $failures++
        }
    }
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures -gt 0) {
    Write-Host "$failures of $($checks.Count) temp-clean check(s) failed."
    exit 1
}

Write-Host "$($checks.Count) temp-clean check(s) passed."
