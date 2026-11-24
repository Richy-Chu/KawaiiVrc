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
# The WebView2 profile is the separate exception: its browser holds it open until the host exits,
# so it lives in vrc-companion-browser-<pid> and the next run's sweep reclaims it. It is judged
# all the same - a name this check cannot read is a folder no sweep will ever take, and a pid
# still running means it is not a finished run's folder.
param(
    # The folder the run's scratch lives in; the workflow uses the process temp. A test points
    # this at a sandbox so the machine's own leftovers never decide a case.
    [string] $TempPath = [System.IO.Path]::GetTempPath(),

    # How long to give a run whose host fell back to its exit handler. Zero judges at once.
    [int] $WaitSeconds = 20
)
$ErrorActionPreference = "Stop"

$runFolder = "vrc-companion-tests-"
$browserFolder = "vrc-companion-browser-"
$deferralNote = "cleanup-deferred.txt"

# How long a deferral may stand before this check takes it itself.
$staleAfterHours = 2

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
    $staleHours = 0.0
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
            $age = ((Get-Date).ToUniversalTime() - $when.ToUniversalTime()).TotalHours
            if ($age -gt $staleAfterHours) { $staleHours = [math]::Floor($age) }
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

    return [pscustomobject]@{ Held = $held; Problem = $problem; StaleHours = $staleHours }
}

function Test-OwnerIsGone([int] $owner) {
    return $null -eq (Get-Process -Id $owner -ErrorAction SilentlyContinue)
}

$failed = @()
$waiting = 0
foreach ($folder in $leftovers) {
    $owner = 0
    $namedForPid = $folder.Name -match ('^' + [regex]::Escape($runFolder) + '(\d+)$')
    if (-not $namedForPid -or -not [int]::TryParse($Matches[1], [ref] $owner)) {
        $failed += $folder.Name + " (not a run folder this check can name)"
        continue
    }

    if (-not (Test-OwnerIsGone $owner)) {
        $failed += $folder.Name + " (process " + $owner + " is still running)"
        continue
    }

    $deferral = Get-Deferral $folder $owner
    if ($null -eq $deferral) {
        $failed += $folder.Name + " (no " + $deferralNote + " naming its own pid)"
        continue
    }

    if ($null -ne $deferral.Problem) {
        $failed += $folder.Name + " (" + $deferral.Problem + ")"
        continue
    }

    # The note is a good one and old: nothing swept it, so this check does. A folder it cannot
    # take is a failure in its own right - the point is that nothing stands, not that the report
    # is quiet.
    if ($deferral.StaleHours -gt 0) {
        try {
            Remove-Item -LiteralPath $folder.FullName -Recurse -Force -ErrorAction Stop
        }
        catch {
            $failed += $folder.Name + " (its note is " + $deferral.StaleHours +
                " hours old and it could not be taken: " + $_.Exception.Message + ")"
            continue
        }

        # Remove-Item can report no error and still leave the folder, and a take that quietly
        # skipped would read exactly like one that worked: the folder has to be gone.
        if (Test-Path -LiteralPath $folder.FullName) {
            $failed += $folder.Name + " (its note is " + $deferral.StaleHours +
                " hours old and it could not be taken: the folder is still there)"
            continue
        }

        Write-Host ("Took stale deferral " + $folder.Name + ": its note is " + $deferral.StaleHours +
            " hours old, so the sweep that should have taken it never ran.")
        continue
    }

    $waiting++
    Write-Host ("Run folder " + $folder.Name + " waits for the next run's sweep: it still held " +
        $deferral.Held.Count + " empty .tmp file(s), " + (($deferral.Held -replace '\s+-?\d+$', '') -join ', '))
}

# The browser profile outlives its host by design, so it is not litter while its pid is gone: the
# next sweep takes it. It is judged like anything else - a pid it cannot read will never sweep, and
# a pid still running is not a finished run's folder.
$acceptedProfiles = @()
foreach ($folder in Get-ScratchLeftover $browserFolder) {
    $owner = 0
    $namedForPid = $folder.Name -match ('^' + [regex]::Escape($browserFolder) + '(\d+)$')
    if (-not $namedForPid -or -not [int]::TryParse($Matches[1], [ref] $owner)) {
        $failed += $folder.Name + " (not a browser folder this check can name)"
        continue
    }

    if (-not (Test-OwnerIsGone $owner)) {
        $failed += $folder.Name + " (process " + $owner + " is still running)"
        continue
    }

    $acceptedProfiles += $folder
}

if ($failed.Count -gt 0) {
    Write-Host "The suite left scratch behind in $TempPath :"
    $failed | ForEach-Object { Write-Host ("  " + $_) }
    Write-Host ("A run must delete its own folders. A run folder may stand only with a " + $deferralNote +
        " that names its own dead pid, holds nothing but empty .tmp files, and the folder holds" +
        " nothing the note does not name - and this check takes one whose note is older than " +
        $staleAfterHours + " hours, since the sweep never did. A browser profile may stand only" +
        " while its pid is gone, so that the next run's sweep takes it.")
    exit 1
}

$acceptedProfiles | ForEach-Object {
    Write-Host ("Browser profile " + $_.Name + " stays until the next run sweeps it: its browser holds it open.")
}

if ($waiting -eq 0) {
    Write-Host "No run folder left in $TempPath."
}
