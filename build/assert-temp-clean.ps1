# Fails when a suite run left its folder in %TEMP%. The suite points the process's temp at
# vrc-companion-tests-<pid> when it starts, so everything a run writes - store files, media
# caches, loose GUID-named files - lands inside that one folder and goes with it.
# The WebView2 profile is the exception: the browser process holds it open until the host
# exits, so a run cannot delete it. It lives in vrc-companion-browser-<pid> and the next run's
# sweep reclaims it once this process is gone, which is why it is reported here, not failed.
$ErrorActionPreference = "Stop"

$temp = [System.IO.Path]::GetTempPath()
$runFolder = "vrc-companion-tests-"
$browserFolder = "vrc-companion-browser-"

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

if ($leftovers.Count -gt 0) {
    Write-Host "The suite left its run folder behind in $temp :"
    $leftovers | ForEach-Object {
        $size = (Get-ChildItem -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue |
            Measure-Object -Property Length -Sum).Sum
        Write-Host ("  " + $_.Name + " (" + [math]::Round($size / 1MB, 1) + " MB)")
    }
    Write-Host "Set VRCC_KEEP_TEST_SCRATCH=1 deliberately to keep a run's files; a normal run must sweep them."
    exit 1
}

Get-ScratchLeftover $browserFolder | ForEach-Object {
    Write-Host ("Browser profile " + $_.Name + " stays until the next run sweeps it: its browser holds it open.")
}

Write-Host "No run folder left in $temp."
