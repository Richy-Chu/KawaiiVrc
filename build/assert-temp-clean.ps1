# Fails when a suite run left scratch behind in %TEMP%. The suite points the process's temp at
# vrc-companion-tests-<pid> when it starts, so everything a run writes - store files, media
# caches, loose GUID-named files - lands inside that one folder, and the run deletes it as its
# last collection ends.
#
# One residue cannot be deleted from inside the host: Chromium's own zero-byte <guid>.tmp files,
# which WebView2's browser process keeps open for as long as the host lives. A run that hits them
# leaves its folder in place with a note naming its own pid and what was still held, and the next
# run's sweep takes the folder. That recorded case is the only one that passes here - a folder
# without a note is a run whose cleanup never ran at all, and it fails the job.
#
# The WebView2 profile is the separate exception: its browser holds it open until the host
# exits, so it lives in vrc-companion-browser-<pid> and the next run's sweep reclaims it, which
# is why it is reported here, not failed.
$ErrorActionPreference = "Stop"

$temp = [System.IO.Path]::GetTempPath()
$runFolder = "vrc-companion-tests-"
$browserFolder = "vrc-companion-browser-"
$deferralNote = "cleanup-deferred.txt"

function Get-ScratchLeftover([string] $prefix) {
    return @(Get-ChildItem -LiteralPath $temp -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase) })
}

# The run deletes its folder as its last collection ends, so a leftover is a real one: the
# wait only covers a run whose host fell back to its exit handler.
$deadline = (Get-Date).AddSeconds(20)
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
    return $text
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

    $note = Get-Deferral $folder $owner
    if ($null -eq $note) {
        $failed += $folder.Name + " (no " + $deferralNote + " naming its own pid)"
        continue
    }

    $reason = @($note -split "`r?`n" | Where-Object { $_ -notmatch '^(host pid|utc):' })
    Write-Host ("Run folder " + $folder.Name + " waits for the next run's sweep: " + ($reason -join ' ').Trim())
}

if ($failed.Count -gt 0) {
    Write-Host "The suite left its run folder behind in $temp :"
    $failed | ForEach-Object { Write-Host ("  " + $_) }
    Write-Host "A run must delete that folder; the only folder allowed to stay is one whose note names its own dead pid."
    exit 1
}

Get-ScratchLeftover $browserFolder | ForEach-Object {
    Write-Host ("Browser profile " + $_.Name + " stays until the next run sweeps it: its browser holds it open.")
}

if ($leftovers.Count -eq 0) {
    Write-Host "No run folder left in $temp."
}
