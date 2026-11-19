$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot '..\shell\target\release\kawaii-shell.exe'

# Only count WebView2 processes this launch creates, not the ones the WPF app
# or any other program already runs.
$before = @{}
Get-Process msedgewebview2 -ErrorAction SilentlyContinue | ForEach-Object { $before[$_.Id] = $true }

$process = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 14
$process.Refresh()
if ($process.HasExited) {
    "EXITED code=$($process.ExitCode)"
    exit 1
}

$ws = [math]::Round($process.WorkingSet64 / 1MB, 1)
$fresh = @(Get-Process msedgewebview2 -ErrorAction SilentlyContinue | Where-Object { -not $before.ContainsKey($_.Id) })
$sum = ($fresh | Measure-Object -Property WorkingSet64 -Sum).Sum
$privateSum = ($fresh | Measure-Object -Property PrivateMemorySize64 -Sum).Sum
"shell core working set: $ws MB"
"new webview2 processes: $($fresh.Count), working set $([math]::Round($sum / 1MB, 1)) MB, private $([math]::Round($privateSum / 1MB, 1)) MB"
"shell private total: $([math]::Round(($process.PrivateMemorySize64 + $privateSum) / 1MB, 1)) MB"
Stop-Process -Id $process.Id -Force
Start-Sleep -Seconds 3
"killed pid $($process.Id)"
