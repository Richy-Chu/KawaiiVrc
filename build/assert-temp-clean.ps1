# Fails when a suite run left scratch behind in %TEMP%. The suite points the process's temp at
# vrc-companion-tests-<pid> when it starts, so everything a run writes - store files, media
# caches, loose GUID-named files - lands inside that one folder, and the run deletes it as its
# last collection ends.
#
# One residue cannot be deleted from inside the host: Chromium's own zero-byte <guid>.tmp files,
# which WebView2's browser process keeps open for as long as the host lives. A run that hits them
# leaves its folder in place with a note naming its own pid and every file that was still held,
# and the next run's sweep takes the folder. That is the only folder allowed to stay, and only
# when the note says nothing but empty .tmp files were in the way: a note naming a locked store
# file is a leak the suite must not paper over, and it fails the job.
#
# The WebView2 profile is the separate exception: its browser holds it open until the host
# exits, so it lives in vrc-companion-browser-<pid> and the next run's sweep reclaims it, which
# is why it is reported here, not failed.
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

    $problem = $null
    if ($held.Count -eq 0) {
        $problem = "nothing at all - the note cannot show what was in the way"
    }

    foreach ($entry in $held) {
        if ($null -ne $problem) { break }
        if ($entry -notmatch '^(?<name>.+) (?<size>-?\d+)$') {
            $problem = "an entry it cannot read: " + $entry
            break
        }

        $name = $Matches['name']
        $size = [long] $Matches['size']
        if ($name -notmatch '\.tmp$') {
            $problem = "a locked file that is not a .tmp: " + $name
            break
        }

        if ($size -ne 0) {
            $problem = "a .tmp file that is not empty: " + $name
            break
        }

        # A file that is still listed has to match what the note says about it.
        $file = Join-Path $folder.FullName $name
        if ((Test-Path -LiteralPath $file -PathType Leaf) -and (Get-Item -LiteralPath $file -Force).Length -ne 0) {
            $problem = "a .tmp file that is not empty now: " + $name
            break
        }
    }

    return [pscustomobject]@{ Held = $held; Problem = $problem }
}

function Test-OwnerIsGone([int] $owner) {
    return $null -eq (Get-Process -Id $owner -ErrorAction SilentlyContinue)
}

$failed = @()
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
        $failed += $folder.Name + " (its note says it still held " + $deferral.Problem + ")"
        continue
    }

    Write-Host ("Run folder " + $folder.Name + " waits for the next run's sweep: it still held " +
        $deferral.Held.Count + " empty .tmp file(s), " + (($deferral.Held -replace '\s+-?\d+$', '') -join ', '))
}

if ($failed.Count -gt 0) {
    Write-Host "The suite left its run folder behind in $TempPath :"
    $failed | ForEach-Object { Write-Host ("  " + $_) }
    Write-Host ("A run must delete that folder. The only folder allowed to stay holds a " + $deferralNote +
        " naming its own dead pid and nothing but empty .tmp files.")
    exit 1
}

Get-ScratchLeftover $browserFolder | ForEach-Object {
    Write-Host ("Browser profile " + $_.Name + " stays until the next run sweeps it: its browser holds it open.")
}

if ($leftovers.Count -eq 0) {
    Write-Host "No run folder left in $TempPath."
}
