# Proves the guard's verdict reaches a filtered run's exit code: when the guard finds a
# change in the profile and no app is running, the run must go red. The probe never writes to
# the real profile - it appends one entry to the recorded baseline (the run's own file under
# %TEMP%) so the profile is missing a file the baseline claims, and the guard reports that
# removal. Nothing is written to %LocalAppData%\VrcCompanion.
$ErrorActionPreference = "Stop"

$app = @(Get-Process -Name KawaiiVrc, VrcCompanion -ErrorAction SilentlyContinue)
if ($app.Count -gt 0) {
    throw "Close KawaiiVrc first: while the app is running the guard reports an inconclusive run, not a leak."
}

$project = Join-Path (Split-Path -Parent $PSScriptRoot) "VrcCompanion.Tests\VrcCompanion.Tests.csproj"
# Build output stays visible: on a cold runner a failed restore is the likeliest failure, and
# the exit code alone would not say which project or package broke.
dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "The test project did not build (exit $LASTEXITCODE)." }

$baseline = Join-Path ([System.IO.Path]::GetTempPath()) "VrcCompanionTests\logs\profile-baseline.txt"
$probeEntry = "profile-guard-probe.json"

# The run clears this file before VSTest, so an older run's record can never be mistaken for
# the one this run writes.
Remove-Item -LiteralPath $baseline -Force -ErrorAction SilentlyContinue

$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = "dotnet"
$startInfo.Arguments = "test `"$project`" -c Release --no-build --nologo --filter `"FullyQualifiedName~MainViewModel`""
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.UseShellExecute = $false
$process = [System.Diagnostics.Process]::Start($startInfo)
$stdout = $process.StandardOutput.ReadToEndAsync()
$stderr = $process.StandardError.ReadToEndAsync()

# The run records the baseline when its test host starts and reads it back after the tests, so
# one entry appended once the file has settled is seen by the guard-only pass. A machine with
# no installed profile records an empty baseline, so this waits for the file to exist and stop
# changing, never for it to have content.
$deadline = (Get-Date).AddMinutes(3)
$recorded = $false
while ((Get-Date) -lt $deadline) {
    if (Test-Path -LiteralPath $baseline) {
        $written = (Get-Item -LiteralPath $baseline).LastWriteTimeUtc
        if ($written -lt (Get-Date).ToUniversalTime().AddSeconds(-1)) {
            $recorded = $true
            break
        }
    }
    Start-Sleep -Milliseconds 100
}
if (-not $recorded) {
    $process.Kill()
    throw "The run never recorded a baseline at $baseline, so the guard-only pass cannot be proven."
}
Add-Content -LiteralPath $baseline -Value ("f|0|0|" + $probeEntry)

if (-not $process.WaitForExit(300000)) {
    $process.Kill()
    throw "The filtered run did not finish within five minutes."
}

$output = @(($stdout.Result + "`n" + $stderr.Result) -split "`r?`n")
$text = $output -join "`n"

$failures = 0
if ($process.ExitCode -eq 0) {
    Write-Host "The filtered run stayed green: the guard's verdict did not reach the exit code."
    $failures++
}
if ($text -notlike "*The real-profile guard failed*") {
    Write-Host "The run failed, but not through the guard target."
    $failures++
}

# Only the injected removal may be reported. Any other change would mean something else
# disturbed the watched profile, and this run would prove nothing.
# The guard indents its list by two spaces and MSBuild re-indents the child's message
# again, so the report is matched by the words, not by a fixed indent.
$changes = @([regex]::Matches($text, "(?m)^\s*(added|modified|removed) (file|directory) (.+?)\s*$") |
    ForEach-Object { $_.Value.Trim() } | Select-Object -Unique)
$expected = "removed file $probeEntry"
if ($changes.Count -ne 1 -or $changes[0] -ne $expected) {
    Write-Host ("The guard reported [" + ($changes -join "; ") + "], expected exactly [" + $expected + "].")
    $failures++
}

if ($failures -gt 0) {
    $output | ForEach-Object { Write-Host $_ }
    throw "The filtered run did not fail the way the guard's verdict requires."
}

Write-Host "PASS: a filtered run exited $($process.ExitCode) on the guard's verdict [$expected]."
