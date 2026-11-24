# Checks the verdict of build/assert-temp-clean.ps1 without running the suite: a genuine
# Chromium deferral passes and is left alone, and a folder that only looks deferred fails - no
# note at all, a note naming a locked store file, a note whose .tmp file was not empty. A deferral
# too old for the sweep to still be coming is taken by the check itself, which is why the cases
# assert the folder is gone - but only while nothing else is wrong with the note: a stale folder
# that also names a locked file keeps failing and keeps standing, because that is evidence, and
# a note dated hours ahead is held to the same rules before its stamp is even read. Every
# case is planted in a sandbox and the check is pointed at it, so the machine's own %TEMP% never
# decides one. Browser profile folders get the same treatment: a dead pid's profile is allowed to
# wait for the sweep, a name the sweep cannot read and a live pid's folder are not.
#
# The hour a live pid accounts for a folder is planted on both sides, because the check and the
# sweep read one rule: a folder untouched exactly on the hour is still its run's, and one a second
# past it is judged by its note as a dead pid's folder is - the answer the sweep deletes it on.
#
# The two-hour bound is not planted here. Deciding it needs one clock to reach both the note's
# writer and this reader, and the writer is C#; DeferralNoteContractTests hands that one clock to
# both, so the bound is decided there and these cases stay hours away from it.
#
# A note that names only a folder the delete could not take is planted in
# DeferralNoteContractTests too, not here: the case needs a folder a real delete refuses - an empty
# one with its own handle open - and only the project's own writer can name what its trim left.
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
#
# The path the check is pointed at is planted too: a folder that is not there and a file where the
# folder should be both fail with an annotation of their own, because neither is a folder that was
# looked in and found empty.
#
# The guard's own run folders are planted as well. They sit under VrcCompanionTests\logs\guard, one
# per run, and the guard keeps each for a day so a failed run's note can still be read before
# RealProfileWatch.PruneOldRuns takes it at the next run's start. That day is written once, in
# RealProfileWatch.RunRetention, and the check reads it out of that source instead of keeping a
# copy. The cases hold the day from the other side: on it the folder still waits, a second past it
# is taken - no run has started since to prune it - and a name no run makes is refused and kept. The
# case stamps the age on the folder and pins the clock on the check, so the edge is decided here
# rather than by how long the script takes to reach it. The edge is planted at the day the watch
# names rather than read from it, so a change to that one value reddens these cases instead of
# moving them with it.
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

# The age a live pid stops accounting for its folder, stamped on the folder itself because that is
# the last write both this check and the sweep's hour read. Both sides of the hour are planted: the
# edge belongs to whoever reads it, and the two sides have to retire a reused pid at one moment.
$insideTheHour = $clock.AddHours(-1)
$pastTheHour = $clock.AddHours(-1).AddSeconds(-1)

# The day a guard run folder is kept for, as RealProfileWatch.RunRetention names it and the check
# reads it from there. It is stamped on the folder itself, which is the last write the pruner and
# the check both age it by; exactly on it the folder still belongs to the failed run whose note may
# need reading, and a value that moved would redden the cases below rather than follow it.
$onTheGuardDay = $clock.AddDays(-1)
$pastTheGuardDay = $clock.AddDays(-1).AddSeconds(-1)

function Get-Sandbox {
    $sandbox = Join-Path $root ([guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
    return $sandbox
}

# The owner is a pid no process can hold for the cases about a finished run, and this shell's own
# pid for the cases that need a process which is still running.
function Write-RunFile([string] $sandbox, [string] $name, [int] $bytes, [int] $owner = $deadPid) {
    $path = Join-Path (Join-Path $sandbox ("vrc-companion-tests-" + $owner)) $name
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    [System.IO.File]::WriteAllBytes($path, (New-Object byte[] $bytes))
}

function Write-RunNote([string] $sandbox, [string[]] $lines, [int] $owner = $deadPid) {
    $note = Join-Path (Join-Path $sandbox ("vrc-companion-tests-" + $owner)) "cleanup-deferred.txt"
    [System.IO.File]::WriteAllLines($note, $lines)
}

function Write-FolderTouched([string] $sandbox, [string] $name, [datetime] $when) {
    # After everything inside is written: a file written into a folder touches it, so a stamp
    # planted first would not be the age the check reads.
    [System.IO.Directory]::SetLastWriteTimeUtc((Join-Path $sandbox $name), $when)
}

function ProfileFolder([string] $sandbox, [string] $owner) {
    # A profile is a folder of files its browser holds open, not temp files in a run folder: the
    # check must not apply the .tmp rule here, only the name and the owner.
    $folder = Join-Path $sandbox ("vrc-companion-browser-" + $owner)
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    [System.IO.File]::WriteAllBytes((Join-Path $folder "Preferences"), (New-Object byte[] 64))
    return $folder
}

function Write-GuardRun([string] $sandbox, [string] $name, [datetime] $when) {
    # The guard's folders are outside the run's folder, because MSBuild reads them after the run.
    # The baseline is what a run records there; the folder's own last write is the age both the
    # guard's pruner and the check judge it by, so it is stamped after everything inside.
    $folder = Join-Path $sandbox ("VrcCompanionTests\logs\guard\" + $name)
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    [System.IO.File]::WriteAllBytes((Join-Path $folder "profile-baseline.txt"), (New-Object byte[] 16))
    [System.IO.Directory]::SetLastWriteTimeUtc($folder, $when)
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
        # A note that names neither a held file nor a folder it could not delete shows nothing
        # about why the folder is still there, which is the one thing the note is for.
        Name = "a note naming nothing it could not take fails"
        Expected = 1
        Marker = "lists nothing it could not take"
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
        # The other half of the future-stamp rule: a note that will never age is due now, but only
        # once every other rule passes. This one names a locked store file, so it is evidence - the
        # same leak a stale note may not paper over, from the other side of the clock.
        Name = "a note dated hours ahead that names a locked store file still fails and is kept"
        Expected = 1
        Marker = "::error title=Scratch left behind::vrc-companion-tests-$deadPid (0 h old: the note names a locked file that is not a .tmp: settings.json)"
        Kept = "vrc-companion-tests-$deadPid"
        At = $now
        Plant = {
            param($sandbox)
            Write-RunFile $sandbox "11111111-1111-1111-1111-111111111111.tmp" 0
            Write-RunFile $sandbox "settings.json" 412
            Write-RunNote $sandbox @(
                "host pid: $deadPid",
                "utc: $farAhead",
                "held: 11111111-1111-1111-1111-111111111111.tmp 0",
                "held: settings.json 412")
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
        # The edge of the hour, from the side that still belongs to a run: exactly on it the pid
        # accounts for the folder, which is the edge the sweep's own cases decide too. A note this
        # sound would otherwise pass, so only the hour can be why it fails.
        Name = "a run folder whose pid is alive and untouched exactly on the hour fails"
        Expected = 1
        Marker = "::error title=Scratch left behind::vrc-companion-tests-$PID (1 h old: process $PID is still running)"
        Kept = "vrc-companion-tests-$PID"
        At = $now
        Plant = {
            param($sandbox)
            Write-RunFile $sandbox "11111111-1111-1111-1111-111111111111.tmp" 0 $PID
            Write-RunNote $sandbox @(
                "host pid: $PID",
                "utc: $now",
                "held: 11111111-1111-1111-1111-111111111111.tmp 0") $PID
            Write-FolderTouched $sandbox ("vrc-companion-tests-" + $PID) $insideTheHour
        }
    },
    @{
        # The other side: a folder no process has touched for over an hour is a finished run's, even
        # though something still holds its pid, and the sweep deletes it on that same hour. A check
        # with no hour failed this folder for a process that is not its run, and would have reddened
        # a run the next sweep clears - so it is judged by its note, which is sound and recent.
        Name = "a run folder whose pid is alive but untouched past the hour is judged by its note"
        Expected = 0
        Marker = "Run folder vrc-companion-tests-$PID waits for the next run's sweep: it still held 1 empty .tmp file(s), 11111111-1111-1111-1111-111111111111.tmp - pid $PID is alive but has not touched the folder for 1 h, so it is not the run that left it"
        Kept = "vrc-companion-tests-$PID"
        At = $now
        Plant = {
            param($sandbox)
            Write-RunFile $sandbox "11111111-1111-1111-1111-111111111111.tmp" 0 $PID
            Write-RunNote $sandbox @(
                "host pid: $PID",
                "utc: $now",
                "held: 11111111-1111-1111-1111-111111111111.tmp 0") $PID
            Write-FolderTouched $sandbox ("vrc-companion-tests-" + $PID) $pastTheHour
        }
    },
    @{
        # The sweep judges both prefixes with the one hour, so a profile whose pid a stranger
        # inherited is equally due: this check has to let the next sweep take it rather than fail
        # the job for a process that is not the browser.
        Name = "a browser profile whose pid is alive but untouched past the hour waits for the sweep"
        Expected = 0
        Marker = "Browser profile vrc-companion-browser-$PID stays until the next run sweeps it: pid $PID is alive but has not touched it for 1 h, so the browser that held it is gone."
        Kept = "vrc-companion-browser-$PID"
        At = $now
        Plant = {
            param($sandbox)
            ProfileFolder $sandbox $PID | Out-Null
            Write-FolderTouched $sandbox ("vrc-companion-browser-" + $PID) $pastTheHour
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
    },
    @{
        # The day the guard keeps a folder for belongs to RealProfileWatch.RunRetention, and the
        # check reads it out of that source: on it the folder is still the record of a run that may
        # have failed, so it waits. The note a failure is read from is in this folder, which is why
        # the day is not shortened.
        Name = "a guard run folder exactly on the day still waits for its note to be read"
        Expected = 0
        Marker = "Guard run folder 0123456789abcdef0123456789abcdef stays for the day a failed run's note is read from: it is 24 h old."
        Kept = "VrcCompanionTests\logs\guard\0123456789abcdef0123456789abcdef"
        At = $now
        Plant = {
            param($sandbox)
            Write-GuardRun $sandbox "0123456789abcdef0123456789abcdef" $onTheGuardDay
        }
    },
    @{
        # No run has started since to prune it - the run that made it was the last one, and it
        # crashed or was killed - so the folder would stand for as long as nothing else runs the
        # suite. The check takes it, as it takes a stale deferral no sweep came for.
        Name = "a guard run folder a second past the day is taken by the check"
        Expected = 0
        Marker = "Took guard run folder 0123456789abcdef0123456789abcdef: its folder is 24 h old, past the day the next run prunes one by, so no run has started since."
        Gone = "VrcCompanionTests\logs\guard\0123456789abcdef0123456789abcdef"
        At = $now
        Plant = {
            param($sandbox)
            Write-GuardRun $sandbox "0123456789abcdef0123456789abcdef" $pastTheGuardDay
        }
    },
    @{
        # PruneOldRuns only touches a folder named the way the project and a test host name one, so
        # a name it cannot read is a folder nothing will ever take. It is evidence rather than
        # litter: the check refuses it and leaves it where it is - the name rule wins over the age.
        Name = "a guard folder no run names fails and is kept"
        Expected = 1
        Marker = "::error title=Scratch left behind::crashed-run (24 h old: not a guard run folder this check can name)"
        Kept = "VrcCompanionTests\logs\guard\crashed-run"
        At = $now
        Plant = {
            param($sandbox)
            Write-GuardRun $sandbox "crashed-run" $pastTheGuardDay
        }
    },
    @{
        # "No run folder left" is the one verdict this check may not give about a folder it never
        # saw: a path that is not there has to fail instead of reading as an empty one.
        Name = "a temp path that does not exist fails instead of passing as clean"
        Expected = 1
        Path = "no-such-folder"
        Marker = "::error title=Scratch could not be checked::Scratch could not be checked: the path does not exist"
        Summary = @(
            "| Path | Why nothing was judged |",
            "| the path does not exist |")
        Plant = {}
    },
    @{
        # A file is not a folder, and Test-Path alone would not say so: only a check that asks for a
        # folder decides this one.
        Name = "a temp path that is a file fails"
        Expected = 1
        Path = "not-a-folder"
        Marker = "::error title=Scratch could not be checked::Scratch could not be checked: the path is a file, not a folder"
        Summary = @("| the path is a file, not a folder |")
        Plant = {
            param($sandbox)
            [System.IO.File]::WriteAllBytes((Join-Path $sandbox "not-a-folder"), (New-Object byte[] 8))
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
        # A case about the path itself points the check at something other than the sandbox.
        $target = $sandbox
        if (-not [string]::IsNullOrWhiteSpace($checkCase.Path)) {
            $target = Join-Path $sandbox $checkCase.Path
        }
        Remove-Item -LiteralPath $summary -Force -ErrorAction SilentlyContinue
        $env:GITHUB_STEP_SUMMARY = $summary
        $result = Invoke-Check $target $checkCase.At
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
        # The title is not fixed - a path it could not judge is not a folder left behind - so a case
        # whose wording matters pins the whole annotation line through Marker.
        $annotations = ([regex]::Matches($result.Text, "(?m)^::error ")).Count
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
