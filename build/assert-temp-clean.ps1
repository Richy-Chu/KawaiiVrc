# Fails when a suite run left scratch behind in %TEMP%. The suite points the process's temp at
# vrc-companion-tests-<pid> when it starts, so everything a run writes - store files, media
# caches, loose GUID-named files - lands inside that one folder, and the run deletes it as its
# last collection ends.
#
# One residue cannot be deleted from inside the host: Chromium's own zero-byte <guid>.tmp files,
# which WebView2's browser process keeps open for as long as the host lives. A run that hits them
# leaves its folder in place with a note naming its own pid and every file that was still held,
# and the next run's sweep takes the folder. That is the only folder allowed to stay, and only
# when the note says nothing but empty .tmp files were in the way, and the folder holds nothing
# the note does not name: a note naming a locked store file is a leak the suite must not paper
# over, and so is a store file left beside the temp files with no line about it.
#
# A deferral is not a licence to keep the folder for good: the note records when the run gave up,
# and one older than a couple of hours was not taken by the next run's sweep. This check takes it
# itself - locally that usually just means nobody has run the suite since - and it only reports a
# folder it could not take. A stale note that names something the note below would refuse is still
# a failure, and still keeps the folder: that is evidence, not litter.
#
# A note dated ahead of this check is the same problem from the other side: its age is negative, so
# it only ever walks towards the two-hour bound and never reaches it, and the folder stands for as
# long as the stamp is wrong. Skew between the run's clock and this one is seconds, so a note a few
# minutes ahead is believed and ages into the past on its own; anything further is a stamp that
# cannot be an age at all, so the note is due now and the check takes it - once every other rule
# passes, because a wrong stamp is not a leak.
#
# A pid in a folder's name accounts for that folder only while its process runs and the folder has
# been touched since: Windows hands a finished run's pid to an unrelated process, which would
# otherwise keep a finished run's litter for as long as it runs. Past an hour untouched the folder
# is a finished run's and is judged by its note like any other - the same hour the sweep retires it
# by, because a check that failed such a folder would redden a run the next sweep takes.
#
# The WebView2 profile is the separate exception: its browser holds it open until the host exits,
# so it lives in vrc-companion-browser-<pid> and the next run's sweep reclaims it. It is judged
# all the same: a name this check cannot read is a folder no sweep will ever take, and a pid that
# is still running and has touched it inside the hour is not a finished run's folder.
#
# The guard's own folders are the one thing outside the run's folder: MSBuild reads them after the
# run, from a process the run's temp redirect never reaches, so they live under
# %TEMP%\VrcCompanionTests\logs\guard\<run>. Each run makes one and keeps it for a day, so a failed
# run's note can still be read, and RealProfileWatch's PruneOldRuns takes one older than that when
# the next run starts. A folder left past that day had no run after it to prune it - the run that
# made it was the last one in this job, and it crashed or was killed - so this check takes it
# itself; a name no run makes is one no run will ever prune, so it is refused and kept.
#
# The path itself is judged before anything inside it: a -TempPath that does not exist, names a
# file, or cannot be listed is not a folder that held nothing, and "No run folder left" is the one
# verdict this check may not give about a folder it never saw.
param(
    # The folder the run's scratch lives in; the workflow uses the process temp. A test points
    # this at a sandbox so the machine's own leftovers never decide a case.
    [string] $TempPath = [System.IO.Path]::GetTempPath(),

    # How long to give a run whose host fell back to its exit handler. Zero judges at once.
    [int] $WaitSeconds = 20,

    # The moment a deferral is aged against, as an ISO 8601 timestamp in UTC. A test pins it so a
    # bound - the two-hour threshold or the skew grace - is decided on its exact edge, not at
    # whatever the machine's clock said; a run leaves it out and the check reads the clock.
    [string] $Now = ""
)
$ErrorActionPreference = "Stop"

$runFolder = "vrc-companion-tests-"
$browserFolder = "vrc-companion-browser-"
$deferralNote = "cleanup-deferred.txt"

# How long a deferral may stand before this check takes it itself.
$staleAfterHours = 2

# How long a folder may go untouched before the pid in its name stops accounting for it. Windows
# hands a finished run's pid to an unrelated process, and TestProfile.SweepFinishedRuns retires a
# folder on exactly this hour; reading it any other way here would judge a folder differently from
# the sweep that is meant to take it.
$abandonedAfterHours = 1

# How long a guard run folder is kept before this check takes it, in hours so that it is aged by
# the same measure as every other folder here. RealProfileWatch.PruneOldRuns deletes one past this
# day at the next run's start, so a folder within it is still the record a failed run's note is
# read from; past it, no run has started to prune it.
$guardKeptHours = 24

# How far ahead of the clock a note may be dated and still be read as a recent deferral: ordinary
# skew is seconds, so a note within this ages into the past on its own. Past it the age counts up
# towards zero instead, and the folder would stand until someone deleted it by hand.
$futureGraceMinutes = 5

# One reading for the whole check, so a deferral's age does not depend on how long a wait took.
$clock = (Get-Date).ToUniversalTime()
if (-not [string]::IsNullOrWhiteSpace($Now)) {
    $pinned = [datetime]::MinValue
    if (-not [datetime]::TryParse(
            $Now,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::RoundtripKind,
            [ref] $pinned)) {
        throw "The -Now value is not an ISO 8601 timestamp: " + $Now
    }

    $clock = $pinned.ToUniversalTime()
}

function Get-ScratchLeftover([string] $prefix) {
    return @(Get-ChildItem -LiteralPath $TempPath -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase) })
}

# The run deletes its folder as its last collection ends, so a leftover is a real one: the
# wait only covers a run whose host fell back to its exit handler.
$deadline = (Get-Date).AddSeconds($WaitSeconds)
$leftovers = Get-ScratchLeftover $runFolder
while ($leftovers.Count -gt 0 -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    $leftovers = Get-ScratchLeftover $runFolder
}

function Get-Deferral([System.IO.DirectoryInfo] $folder, [int] $owner) {
    $note = Join-Path $folder.FullName $deferralNote
    if (-not (Test-Path -LiteralPath $note -PathType Leaf)) { return $null }
    $text = Get-Content -LiteralPath $note -Raw -ErrorAction SilentlyContinue
    # The note has to name the pid the folder is named for: another pid proves nothing.
    if ($null -eq $text -or $text -notmatch ('(?m)^host pid: ' + $owner + '\r?$')) { return $null }

    $held = @($text -split "`r?`n" |
        Where-Object { $_ -match '^held: ' } |
        ForEach-Object { $_.Substring(6).Trim() })
    $unlisted = @($text -split "`r?`n" |
        Where-Object { $_ -match '^unlisted: ' } |
        ForEach-Object { $_.Substring(10).Trim() })

    # A run that could not read its own folder is not a run that found nothing in it, and the
    # failure has to say which one happened.
    $problem = $null
    if ($unlisted.Count -gt 0) {
        $problem = "the note says the run could not list the folder (" + ($unlisted -join ' ') + ")"
    }
    elseif ($held.Count -eq 0) {
        $problem = "the note lists no held file, so it cannot show what was in the way"
    }

    # The note records when the run gave up, and the sweep takes a deferred folder on the next
    # run's startup. One that has waited much longer than that was not swept, so this check takes
    # it rather than waiting for a sweep that never came. Its age is reported, not judged, because
    # the caller has still to refuse a note with anything else wrong with it - that folder is
    # evidence, and deleting it would hide what the suite got wrong.
    #
    # A stamp ahead of the clock is the same folder from the other side: an age that is negative
    # only counts up towards zero, so the bound above is never reached however wrong the stamp is,
    # and the folder would stand for good. A note dated further ahead than any skew is one whose
    # age cannot be believed, so it is due now - and both answers wait for every rule below, since
    # a wrong stamp is not a leak and this check deletes litter, never evidence.
    $staleHours = 0.0
    $futureStamp = $null
    if ($null -eq $problem) {
        # Not wrapped in @(): an empty array is not $null, and a note with no such line has to
        # read as one with no line, not as a line that would not parse.
        $stamp = $text -split "`r?`n" |
            Where-Object { $_ -match '^utc: ' } |
            ForEach-Object { $_.Substring(5).Trim() } |
            Select-Object -First 1
        $when = [datetime]::MinValue
        if ([string]::IsNullOrWhiteSpace($stamp)) {
            $problem = "the note has no utc line, so its age cannot be judged"
        }
        elseif (-not [datetime]::TryParse(
                $stamp,
                [System.Globalization.CultureInfo]::InvariantCulture,
                [System.Globalization.DateTimeStyles]::RoundtripKind,
                [ref] $when)) {
            $problem = "the note's utc line cannot be read: " + $stamp
        }
        else {
            $age = ($clock - $when.ToUniversalTime()).TotalHours
            if ($age -gt $staleAfterHours) {
                $staleHours = [math]::Floor($age)
            }
            elseif (($when.ToUniversalTime() - $clock) -gt [timespan]::FromMinutes($futureGraceMinutes)) {
                # Ticks, not hours: a stamp exactly on the grace is one skew can still explain, and
                # dividing it into hours would decide that edge on a float's last bit.
                $futureStamp = $stamp
            }
        }
    }

    foreach ($entry in $held) {
        if ($null -ne $problem) { break }
        if ($entry -notmatch '^(?<name>.+) (?<size>-?\d+)$') {
            $problem = "the note has an entry it cannot read: " + $entry
            break
        }

        $name = $Matches['name']
        $size = [long] $Matches['size']
        if ($name -notmatch '\.tmp$') {
            $problem = "the note names a locked file that is not a .tmp: " + $name
            break
        }

        if ($size -ne 0) {
            $problem = "the note names a .tmp file that is not empty: " + $name
            break
        }

        # A file that is still listed has to match what the note says about it.
        $file = Join-Path $folder.FullName $name
        if ((Test-Path -LiteralPath $file -PathType Leaf) -and (Get-Item -LiteralPath $file -Force).Length -ne 0) {
            $problem = "the note names a .tmp file that is not empty now: " + $name
            break
        }
    }

    # The note says what blocked the delete, not what is in the folder, and the run deletes every
    # file it can before writing it: anything else still here is something the run could not take
    # with it. A store file beside the browser's temp files is a leak however short the note is, so
    # a file the note does not name fails the deferral. Directories may stand - the browser makes
    # its own - and their files are judged by this same walk.
    if ($null -eq $problem) {
        $note = Join-Path $folder.FullName $deferralNote
        try {
            $unnamed = @(Get-ChildItem -LiteralPath $folder.FullName -File -Recurse -Force -ErrorAction Stop |
                Where-Object { $_.FullName -ne $note -and ($_.Name -notmatch '\.tmp$' -or $_.Length -ne 0) })
        }
        catch {
            $problem = "the folder could not be listed now (" + $_.Exception.GetType().Name + ": " + $_.Exception.Message + ")"
        }

        if ($null -eq $problem -and $unnamed.Count -gt 0) {
            $problem = "the folder holds " + $unnamed.Count + " file(s) the note does not: " +
                (($unnamed | ForEach-Object {
                    $_.FullName.Substring($folder.FullName.Length).TrimStart('\', '/') + " (" + $_.Length + " bytes)"
                }) -join ', ')
        }
    }

    return [pscustomobject]@{
        Held = $held
        Problem = $problem
        StaleHours = $staleHours
        FutureStamp = $futureStamp
    }
}

function Test-OwnerIsGone([int] $owner) {
    return $null -eq (Get-Process -Id $owner -ErrorAction SilentlyContinue)
}

# A failure has to say the same thing to the run page as to the log: which folder, how long it has
# been standing, and why it is still there. The age is the note's own where the note has one - that
# is the age the two-hour rule reads - and the folder's last write otherwise.
function Get-ScratchFailure([string] $name, [double] $ageHours, [string] $reason) {
    return [pscustomobject]@{
        Name = $name
        AgeHours = [math]::Max(0, $ageHours)
        Reason = $reason
    }
}

function Get-FolderAge([System.IO.DirectoryInfo] $folder) {
    return ($clock - $folder.LastWriteTimeUtc).TotalHours
}

# Whether the pid in a folder's name still accounts for that folder: gone, running, or abandoned -
# a live process that has not touched the folder inside the hour. A folder a stranger has inherited
# the pid of is a finished run's litter, and the sweep deletes it after the same hour, so this check
# judges it by its note instead of failing it for a process that is not its run.
function Get-OwnerState([int] $owner, [System.IO.DirectoryInfo] $folder) {
    if (Test-OwnerIsGone $owner) { return "gone" }
    if ((Get-FolderAge $folder) -gt $abandonedAfterHours) { return "abandoned" }
    return "running"
}

function Get-HoursText([double] $hours) {
    return $hours.ToString("0.#", [System.Globalization.CultureInfo]::InvariantCulture) + " h"
}

# GitHub reads %, CR and LF as escapes inside an annotation, so they travel encoded.
function Get-AnnotationText([string] $text) {
    return ($text -replace '%', '%25' -replace "`r", '%0D' -replace "`n", '%0A')
}

# A pipe or a line break would end a table cell early and shift every column after it.
function Get-TableCell([string] $text) {
    return ($text -replace "`r?`n", ' ' -replace '\|', '\|')
}

# The rule in one place: a path this check cannot judge and a folder it refuses are both red on
# the run page, and both have to say what a folder was allowed to be.
$rule = "A run must delete its own folders. A run folder may stand only with a " + $deferralNote +
    " that names its own pid, holds nothing but empty .tmp files, and the folder holds" +
    " nothing the note does not name - and this check takes one whose note is older than " +
    $staleAfterHours + " hours or dated further ahead than clock skew explains, since neither" +
    " kind ever ages into a sweep. A pid only accounts for a folder it has touched inside the" +
    " hour, since Windows hands a finished run's pid to another process, and a browser profile" +
    " may stand only while no pid accounts for it, so that the next run's sweep takes it. A guard" +
    " run folder may stand only for the day the next run prunes one by, since a failed run's note" +
    " may still need reading in it."

# A path this check cannot list has not been seen to hold nothing, and a -TempPath that is missing,
# names a file, or may not be read would otherwise pass as "No run folder left" - the one verdict
# this check may only give about a folder it looked in. It is reported as a failure of its own
# rather than as a folder's, because no folder was left behind: none could be found.
$pathProblem = $null
if ([string]::IsNullOrWhiteSpace($TempPath)) {
    $pathProblem = "the path is empty"
}
elseif (-not (Test-Path -LiteralPath $TempPath -PathType Container)) {
    if (Test-Path -LiteralPath $TempPath) {
        $pathProblem = "the path is a file, not a folder"
    }
    else {
        $pathProblem = "the path does not exist"
    }
}
else {
    try {
        Get-ChildItem -LiteralPath $TempPath -Directory -Force -ErrorAction Stop | Out-Null
    }
    catch {
        $pathProblem = "the folder cannot be listed (" + $_.Exception.GetType().Name + ": " +
            $_.Exception.Message + ")"
    }
}

if ($null -ne $pathProblem) {
    $verdict = "Scratch could not be checked: " + $pathProblem + " (" + $TempPath + ")."
    Write-Host $verdict
    Write-Host $rule

    Write-Host ("::error title=Scratch could not be checked::" + (Get-AnnotationText $verdict))

    # A red step is the only one anybody opens, so the path lands on the run page too.
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
        $tick = [char] 96
        $summaryText = "### Scratch could not be checked" + "`n`n" +
            "| Path | Why nothing was judged |" + "`n" + "| --- | --- |" + "`n" +
            "| " + $tick + (Get-TableCell $TempPath) + $tick + " | " +
            (Get-TableCell $pathProblem) + " |" + "`n`n" + $rule + "`n"
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summaryText
    }

    exit 1
}

# Each failure carries what the run page has to show about it.
$failed = @()
$waiting = 0
foreach ($folder in $leftovers) {
    $owner = 0
    $namedForPid = $folder.Name -match ('^' + [regex]::Escape($runFolder) + '(\d+)$')
    if (-not $namedForPid -or -not [int]::TryParse($Matches[1], [ref] $owner)) {
        $failed += Get-ScratchFailure $folder.Name (Get-FolderAge $folder) "not a run folder this check can name"
        continue
    }

    $state = Get-OwnerState $owner $folder
    if ($state -eq "running") {
        $failed += Get-ScratchFailure $folder.Name (Get-FolderAge $folder) ("process " + $owner + " is still running")
        continue
    }

    $deferral = Get-Deferral $folder $owner
    if ($null -eq $deferral) {
        $failed += Get-ScratchFailure $folder.Name (Get-FolderAge $folder) ("no " + $deferralNote + " naming its own pid")
        continue
    }

    if ($null -ne $deferral.Problem) {
        $age = $deferral.StaleHours
        if ($age -le 0) { $age = Get-FolderAge $folder }
        $failed += Get-ScratchFailure $folder.Name $age $deferral.Problem
        continue
    }

    # The note is a good one and old - or dated so far ahead that it will never age: nothing will
    # sweep either way, so this check does. A folder it cannot take is a failure in its own right -
    # the point is that nothing stands, not that the report is quiet.
    if ($deferral.StaleHours -gt 0 -or $null -ne $deferral.FutureStamp) {
        try {
            Remove-Item -LiteralPath $folder.FullName -Recurse -Force -ErrorAction Stop
        }
        catch {
            $failed += Get-ScratchFailure $folder.Name $deferral.StaleHours ("it could not be taken: " + $_.Exception.Message)
            continue
        }

        # Remove-Item can report no error and still leave the folder, and a take that quietly
        # skipped would read exactly like one that worked: the folder has to be gone.
        if (Test-Path -LiteralPath $folder.FullName) {
            $failed += Get-ScratchFailure $folder.Name $deferral.StaleHours "it could not be taken: the folder is still there"
            continue
        }

        $why = "its note is " + $deferral.StaleHours +
            " hours old, so the sweep that should have taken it never ran."
        if ($null -ne $deferral.FutureStamp) {
            $why = "its note is dated " + $deferral.FutureStamp + ", ahead of this check," +
                " so its age can never reach the " + $staleAfterHours + "-hour bound."
        }

        Write-Host ("Took stale deferral " + $folder.Name + ": " + $why)
        continue
    }

    $waiting++
    $waits = "Run folder " + $folder.Name + " waits for the next run's sweep: it still held " +
        $deferral.Held.Count + " empty .tmp file(s), " + (($deferral.Held -replace '\s+-?\d+$', '') -join ', ')
    if ($state -eq "abandoned") {
        $waits += " - pid " + $owner + " is alive but has not touched the folder for " +
            (Get-HoursText (Get-FolderAge $folder)) + ", so it is not the run that left it"
    }

    Write-Host $waits
}

# The browser profile outlives its host by design, so it is not litter while its pid is gone: the
# next sweep takes it. It is judged like anything else - a pid it cannot read will never sweep, and
# a pid still running is not a finished run's folder.
$acceptedProfiles = @()
foreach ($folder in Get-ScratchLeftover $browserFolder) {
    $owner = 0
    $namedForPid = $folder.Name -match ('^' + [regex]::Escape($browserFolder) + '(\d+)$')
    if (-not $namedForPid -or -not [int]::TryParse($Matches[1], [ref] $owner)) {
        $failed += Get-ScratchFailure $folder.Name (Get-FolderAge $folder) "not a browser folder this check can name"
        continue
    }

    $state = Get-OwnerState $owner $folder
    if ($state -eq "running") {
        $failed += Get-ScratchFailure $folder.Name (Get-FolderAge $folder) ("process " + $owner + " is still running")
        continue
    }

    $acceptedProfiles += [pscustomobject]@{
        Folder = $folder
        Owner = $owner
        Abandoned = ($state -eq "abandoned")
    }
}

# The guard's run folders live under the real temp rather than the run's, because MSBuild reads
# them after VSTest. Each run keeps its own for a day so a failed run's note can still be read, and
# PruneOldRuns takes one older than that at the next run's start: a folder inside the day is the
# record of a run that may have failed and is named, and past it there was no run after it to
# prune it, so this check does. A name no run makes is a folder no run will ever prune.
$guardRoot = Join-Path $TempPath (Join-Path "VrcCompanionTests" (Join-Path "logs" "guard"))
$keptGuardRuns = @()
if (Test-Path -LiteralPath $guardRoot -PathType Container) {
    $guardRuns = @()
    try {
        $guardRuns = @(Get-ChildItem -LiteralPath $guardRoot -Directory -Force -ErrorAction Stop)
    }
    catch {
        # Nothing inside was judged, which is not the same as finding nothing there: a folder that
        # may hold a crashed run's baseline is reported, never passed over as an empty one.
        $failed += Get-ScratchFailure $guardRoot 0 ("the guard's folder cannot be listed (" +
            $_.Exception.GetType().Name + ": " + $_.Exception.Message + ")")
    }

    foreach ($folder in $guardRuns) {
        if ($folder.Name -notmatch '^[0-9a-f]{32}$|^\d+$') {
            $failed += Get-ScratchFailure $folder.Name (Get-FolderAge $folder) "not a guard run folder this check can name"
            continue
        }

        $age = Get-FolderAge $folder
        if ($age -le $guardKeptHours) {
            $keptGuardRuns += $folder
            continue
        }

        try {
            Remove-Item -LiteralPath $folder.FullName -Recurse -Force -ErrorAction Stop
        }
        catch {
            $failed += Get-ScratchFailure $folder.Name $age ("it could not be taken: " + $_.Exception.Message)
            continue
        }

        # A delete that reports no error and leaves the folder would read like one that worked.
        if (Test-Path -LiteralPath $folder.FullName) {
            $failed += Get-ScratchFailure $folder.Name $age "it could not be taken: the folder is still there"
            continue
        }

        Write-Host ("Took guard run folder " + $folder.Name + ": its folder is " + (Get-HoursText $age) +
            " old, past the day the next run prunes one by, so no run has started since.")
    }
}

if ($failed.Count -gt 0) {
    Write-Host "The suite left scratch behind in $TempPath :"
    $failed | ForEach-Object {
        Write-Host ("  " + $_.Name + " (" + (Get-HoursText $_.AgeHours) + " old: " + $_.Reason + ")")
    }

    Write-Host $rule

    # One annotation, not one per folder: a step shows ten and drops the rest, so a run page that
    # named whatever fitted would be worse than one that names the first folder and points at the
    # summary, which carries every folder because nothing there is capped.
    $first = $failed[0]
    $headline = $first.Name + " (" + (Get-HoursText $first.AgeHours) + " old: " + $first.Reason + ")"
    if ($failed.Count -gt 1) {
        $headline += "; " + ($failed.Count - 1) + " more folder(s) in the step summary"
    }

    Write-Host ("::error title=Scratch left behind::" + (Get-AnnotationText $headline))

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
        $rows = ($failed | ForEach-Object {
                "| ``" + (Get-TableCell $_.Name) + "`` | " + (Get-HoursText $_.AgeHours) + " | " +
                    (Get-TableCell $_.Reason) + " |"
            }) -join "`n"
        # [char] 96, because a backtick in the string would escape the quote that ends it.
        $tick = [char] 96
        $summaryText = "### Scratch left behind in " + $tick + $TempPath + $tick + "`n`n" +
            "| Folder | Age | Why it failed |" + "`n" + "| --- | --- | --- |" + "`n" + $rows + "`n`n" +
            $rule + "`n"
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summaryText
    }

    exit 1
}

foreach ($kept in $acceptedProfiles) {
    $line = "Browser profile " + $kept.Folder.Name + " stays until the next run sweeps it: "
    if ($kept.Abandoned) {
        $line += "pid " + $kept.Owner + " is alive but has not touched it for " +
            (Get-HoursText (Get-FolderAge $kept.Folder)) + ", so the browser that held it is gone."
    }
    else {
        $line += "its browser holds it open."
    }

    Write-Host $line
}

# A guard folder inside its day is the record of a run that may have failed: named, with its age,
# so a crashed run's baseline is not lost between the run that left it and the next one's prune.
foreach ($folder in $keptGuardRuns) {
    Write-Host ("Guard run folder " + $folder.Name + " stays for the day a failed run's note is read" +
        " from: it is " + (Get-HoursText (Get-FolderAge $folder)) + " old.")
}

if ($waiting -eq 0) {
    Write-Host "No run folder left in $TempPath."
}
