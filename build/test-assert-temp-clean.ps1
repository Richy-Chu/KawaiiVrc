# Checks the verdict of build/assert-temp-clean.ps1 without running the suite: a genuine
# Chromium deferral passes and is left alone, and a folder that only looks deferred fails - no
# note at all, a note naming a locked store file, a note whose .tmp file was not empty. A deferral
# too old for the sweep to still be coming is taken by the check itself, which is why the cases
# assert the folder is gone - but only while nothing else is wrong with the note: a stale folder
# that also names a locked file keeps failing and keeps standing, because that is evidence. Every
# case is planted in a sandbox and the check is pointed at it, so the machine's own %TEMP% never
# decides one. Browser profile folders get the same treatment: a dead pid's profile is allowed to
# wait for the sweep, a name the sweep cannot read and a live pid's folder are not.
#
# The two-hour bound is not planted here. Deciding it needs one clock to reach both the note's
# writer and this reader, and the writer is C#; DeferralNoteContractTests hands that one clock to
# both, so the bound is decided there and these cases stay hours away from it.
#
# The grace for a stamp ahead of the clock is planted here, because only this reader has one: a
# note exactly on the grace waits, one a second past it is taken, and both pin the clock on the
# check so that edge is a case rather than a race.
#
# A failure also has to land on the run page, not only in the step log: every failing case must
# write exactly one ::error naming its folder, its age and its reason, and a step summary that
# carries the same for each folder, while a passing case writes neither. Each case hands the check
# its own summary file through GITHUB_STEP_SUMMARY, so a planted failure never writes into this
# script's own step summary.
$ErrorActionPreference = "Stop"

$check = Join-Path $PSScriptRoot "assert-temp-clean.ps1"
$shell = (Get-Process -Id $PID).Path
$root = Join-Path ([System.IO.Path]::GetTempPath()) ("kawaiivrc-temp-check-" + [guid]::NewGuid().ToString("N"))

# No process can own this id, so a folder named for it is a finished run's.
$deadPid = 2147483000

# The note's timestamp is what the check ages, so a passing case has to be written now: a fixed
# date would be a stale deferral by the time anyone runs this. The cases that age a note against
# this reading pin it on the check, so a stamp sitting exactly on a bound is decided the same way
# on a fast machine as on a slow one.
$clock = (Get-Date).ToUniversalTime()
$now = $clock.ToString("O")
$stale = $clock.AddHours(-3).ToString("O")
$onTheGrace = $clock.AddMinutes(5).ToString("O")
$pastTheGrace = $clock.AddMinutes(5).AddSeconds(1).ToString("O")
$farAhead = $clock.AddHours(6).ToString("O")

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

function Invoke-Check([string] $sandbox, [string] $at = "") {
    $arguments = @("-NoProfile", "-File", $check, "-TempPath", $sandbox, "-WaitSeconds", "0")
    if (-not [string]::IsNullOrWhiteSpace($at)) { $arguments += @("-Now", $at) }
    $output = @(& $shell @arguments 2>&1)
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
        Kept = "vrc-companion-tests-$deadPid"
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
        # The next run's sweep never came, so the check takes the folder itself: locally that
        # usually just means nobody ran the suite since, and a folder nothing will take is worse
        # than one this check can.
        Name = "a note too old to be waiting for a sweep is taken"
        Expected = 0
        Marker = "Took stale deferral vrc-companion-tests-$deadPid"
        Gone = "vrc-companion-tests-$deadPid"
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
        # Skew is seconds, so a note a few minutes ahead is an honest deferral whose age turns
        # positive on its own: it still waits for the sweep that may yet come.
        Name = "a note dated exactly on the skew grace still waits"
        Expected = 0
        Marker = "waits for the next run's sweep"
        Kept = "vrc-companion-tests-$deadPid"
        At = $now
        Plant = {
            param($sandbox)
            Write-RunFile $sandbox "11111111-1111-1111-1111-111111111111.tmp" 0
            Write-RunNote $sandbox @(
                "host pid: $deadPid",
                "utc: $onTheGrace",
                "held: 11111111-1111-1111-1111-111111111111.tmp 0")
        }
    },
    @{
        # The folder nothing reclaims: its age only walks towards zero, so the two-hour bound is
        # never reached and no sweep is ever due. The note is otherwise sound, so the check takes
        # it - a wrong stamp is not the leak that has to be kept as evidence.
        Name = "a note dated hours ahead is taken"
        Expected = 0
        Marker = "Took stale deferral vrc-companion-tests-${deadPid}: its note is dated $farAhead, ahead of this check, so its age can never reach the 2-hour bound."
        Gone = "vrc-companion-tests-$deadPid"
        At = $now
        Plant = {
            param($sandbox)
            Write-RunFile $sandbox "11111111-1111-1111-1111-111111111111.tmp" 0
            Write-RunNote $sandbox @(
                "host pid: $deadPid",
                "utc: $farAhead",
                "held: 11111111-1111-1111-1111-111111111111.tmp 0")
        }
    },
    @{
        # A second past the grace is past what skew can explain, which pins the grace to the
        # minute rather than merely somewhere near it.
        Name = "a note a second past the skew grace is taken"
        Expected = 0
        Marker = "Took stale deferral vrc-companion-tests-${deadPid}: its note is dated $pastTheGrace, ahead of this check"
        Gone = "vrc-companion-tests-$deadPid"
        At = $now
        Plant = {
            param($sandbox)
            Write-RunFile $sandbox "11111111-1111-1111-1111-111111111111.tmp" 0
            Write-RunNote $sandbox @(
                "host pid: $deadPid",
                "utc: $pastTheGrace",
                "held: 11111111-1111-1111-1111-111111111111.tmp 0")
        }
    },
    @{
        # Age is the only thing that may be forgiven: a note naming a locked store file is the
        # leak the check exists to catch, and taking the folder would destroy the evidence.
        Name = "a stale note naming a locked store file still fails and is kept"
        Expected = 1
        Marker = "not a .tmp"
        Kept = "vrc-companion-tests-$deadPid"
        Plant = {
            param($sandbox)
            Write-RunFile $sandbox "11111111-1111-1111-1111-111111111111.tmp" 0
            Write-RunFile $sandbox "settings.json" 412
            Write-RunNote $sandbox @(
                "host pid: $deadPid",
                "utc: $stale",
                "held: 11111111-1111-1111-1111-111111111111.tmp 0",
                "held: settings.json 412")
        }
    },
    @{
        # The step's log scrolls away, so the folder, its age and the reason have to reach the run
        # page too: the annotation names the first folder, and the summary row carries all three.
        Name = "a refused stale note reports its age and reason to the run page"
        Expected = 1
        Marker = "::error title=Scratch left behind::vrc-companion-tests-$deadPid (3 h old: the note names a locked file that is not a .tmp: settings.json)"
        Kept = "vrc-companion-tests-$deadPid"
        Summary = @(
            "| Folder | Age | Why it failed |",
            "| ``vrc-companion-tests-$deadPid`` | 3 h | the note names a locked file that is not a .tmp: settings.json |")
        Plant = {
            param($sandbox)
            Write-RunFile $sandbox "11111111-1111-1111-1111-111111111111.tmp" 0
            Write-RunFile $sandbox "settings.json" 412
            Write-RunNote $sandbox @(
                "host pid: $deadPid",
                "utc: $stale",
                "held: 11111111-1111-1111-1111-111111111111.tmp 0",
                "held: settings.json 412")
        }
    },
    @{
        Name = "a stale note whose folder holds a file it does not name still fails and is kept"
        Expected = 1
        Marker = "the folder holds 1 file(s) the note does not: settings.json (412 bytes)"
        Kept = "vrc-companion-tests-$deadPid"
        Plant = {
            param($sandbox)
            Write-RunFile $sandbox "11111111-1111-1111-1111-111111111111.tmp" 0
            Write-RunFile $sandbox "settings.json" 412
            Write-RunNote $sandbox @(
                "host pid: $deadPid",
                "utc: $stale",
                "held: 11111111-1111-1111-1111-111111111111.tmp 0")
        }
    },
    @{
        # An unreadable age is not an old one: without the line there is no way to tell a folder
        # from last night from one this run just deferred.
        Name = "a note with no utc line fails"
        Expected = 1
        Marker = "has no utc line, so its age cannot be judged"
        Plant = {
            param($sandbox)
            Write-RunFile $sandbox "11111111-1111-1111-1111-111111111111.tmp" 0
            Write-RunNote $sandbox @(
                "host pid: $deadPid",
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

# The summary file each case hands the check, and the one this process was started with, so the
# real step summary is exactly as it was when this script exits.
$summary = Join-Path $root "summary.md"
$summaryBefore = $env:GITHUB_STEP_SUMMARY

try {
    foreach ($checkCase in $checks) {
        $sandbox = Get-Sandbox
        & $checkCase.Plant $sandbox
        Remove-Item -LiteralPath $summary -Force -ErrorAction SilentlyContinue
        $env:GITHUB_STEP_SUMMARY = $summary
        $result = Invoke-Check $sandbox $checkCase.At
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
            continue
        }

        # Taking a stale folder is half the verdict: what it left or what it took is the other.
        if ($checkCase.Gone -and (Test-Path -LiteralPath (Join-Path $sandbox $checkCase.Gone))) {
            Write-Host ("{0}: left {1} behind, it had to take it" -f $checkCase.Name, $checkCase.Gone)
            $failures++
            continue
        }

        if ($checkCase.Kept -and -not (Test-Path -LiteralPath (Join-Path $sandbox $checkCase.Kept))) {
            Write-Host ("{0}: took {1}, it had to keep it" -f $checkCase.Name, $checkCase.Kept)
            $failures++
            continue
        }

        # One annotation for a failing run, none for a passing one: the run page is part of the
        # verdict, so a check that stopped writing it fails a case here rather than going quiet.
        $annotations = ([regex]::Matches($result.Text, "(?m)^::error title=Scratch left behind::")).Count
        $wanted = 0
        if ($checkCase.Expected -eq 1) { $wanted = 1 }
        if ($annotations -ne $wanted) {
            Write-Host ("{0}: wrote {1} annotation(s), expected {2}" -f $checkCase.Name, $annotations, $wanted)
            Write-Host $result.Text
            $failures++
            continue
        }

        $summaryText = ""
        if (Test-Path -LiteralPath $summary -PathType Leaf) {
            $summaryText = Get-Content -LiteralPath $summary -Raw
        }

        if ($checkCase.Expected -eq 1 -and [string]::IsNullOrWhiteSpace($summaryText)) {
            Write-Host ("{0}: reported a failure without writing the step summary" -f $checkCase.Name)
            $failures++
            continue
        }

        if ($checkCase.Expected -eq 0 -and -not [string]::IsNullOrWhiteSpace($summaryText)) {
            Write-Host ("{0}: wrote a step summary for a passing run" -f $checkCase.Name)
            Write-Host $summaryText
            $failures++
            continue
        }

        # Contains, not -like: the backtick that makes a folder a code span is the wildcard
        # escape character, so a pattern built from the expected row would never match it.
        $missing = @($checkCase.Summary | Where-Object {
                $_ -and $summaryText.IndexOf($_, [System.StringComparison]::Ordinal) -lt 0
            })
        if ($missing.Count -gt 0) {
            Write-Host ("{0}: the step summary did not carry {1}" -f $checkCase.Name, ($missing -join ' | '))
            Write-Host $summaryText
            $failures++
        }
    }
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    if ($null -eq $summaryBefore) {
        Remove-Item Env:GITHUB_STEP_SUMMARY -ErrorAction SilentlyContinue
    }
    else {
        $env:GITHUB_STEP_SUMMARY = $summaryBefore
    }
}

if ($failures -gt 0) {
    Write-Host "$failures of $($checks.Count) temp-clean check(s) failed."
    exit 1
}

Write-Host "$($checks.Count) temp-clean check(s) passed."
