# Re-runs only the tests that failed, once, and reports what passed on the second try. A flaky test
# must not redden a commit that is otherwise good, but it must not be hidden either: the names are
# reported as warnings, and the job stays green only when every one of them ran again and passed.
param(
    # The .trx the first run wrote.
    [string] $Results = "",

    [string] $Project = "VrcCompanion.Tests/VrcCompanion.Tests.csproj",

    [string] $Configuration = "Release",

    # Prints what it would re-run and stops. The script's own test uses this.
    [switch] $PlanOnly
)
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Results)) {
    $Results = Join-Path $env:RUNNER_TEMP "results\first.trx"
}

function Read-Result([string] $path) {
    [xml] $trx = Get-Content -LiteralPath $path -Raw
    # A theory's testName carries its data ("Method(x: 1)"), which no filter expression accepts, so
    # the plan is by method and may re-run a couple of passing cases alongside the failed ones.
    return @($trx.TestRun.Results.UnitTestResult |
        ForEach-Object {
            [pscustomobject]@{
                Name = ([string] $_.testName -split "\(")[0]
                Passed = $_.outcome -eq "Passed"
                Ran = $_.outcome -ne "NotExecuted"
            }
        } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_.Name) })
}

if (-not (Test-Path -LiteralPath $Results -PathType Leaf)) {
    throw ("No test results at " + $Results + ". The first run did not get far enough to fail a test, so there is nothing to retry: look at the step that ran it.")
}

$failed = @(Read-Result $Results | Where-Object { $_.Ran -and -not $_.Passed } | ForEach-Object { $_.Name } | Sort-Object -Unique)
if ($failed.Count -eq 0) {
    throw ("The first run failed but " + $Results + " names no failed test, so the failure was not a test failure.")
}

$filter = ($failed | ForEach-Object { "FullyQualifiedName~" + $_ }) -join "|"
Write-Host ("Retrying " + $failed.Count + " failed test(s): " + ($failed -join ", "))

if ($PlanOnly) {
    Write-Host ("Filter: " + $filter)
    exit 0
}

$directory = Split-Path -Parent $Results
$retried = Join-Path $directory "retry.trx"
if (Test-Path -LiteralPath $retried -PathType Leaf) {
    Remove-Item -LiteralPath $retried -Force
}

& dotnet test $Project -c $Configuration --no-build --nologo `
    --filter $filter `
    --logger "trx;LogFileName=retry.trx" `
    --results-directory $directory
$code = $LASTEXITCODE

# The runner exits 0 when a filter matches nothing, so "no failures" is only evidence once the
# results show every planned test actually ran.
$after = if (Test-Path -LiteralPath $retried -PathType Leaf) { @(Read-Result $retried) } else { @() }
$stillFailing = @($failed | Where-Object {
        $name = $_
        -not ($after | Where-Object { $_.Name -eq $name -and $_.Passed })
    })

if ($code -ne 0 -or $stillFailing.Count -gt 0) {
    Write-Host ("Still failing on retry: " + ($stillFailing -join ", "))
    if ($stillFailing.Count -eq 0) {
        Write-Host ("The retry exited " + $code + " without recording those tests; see " + $retried + ".")
    }

    Write-Host "The retry did not save the suite, so it is red."
    exit 1
}

foreach ($name in $failed) {
    Write-Host ("::warning title=Passed on retry::" + $name)
}

if ($env:GITHUB_STEP_SUMMARY) {
    # The job's own log scrolls away and the badge stays green, so the summary is where a flake is
    # visible afterwards; both runs' trx files are attached to the job for the same reason.
    $summary = "### Passed on retry`n`n" + (($failed | ForEach-Object { "- ``$_``" }) -join "`n") +
        "`n`nThe first run's and the retry's ``.trx`` files are attached to this run as an artifact.`n"
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summary
}

Write-Host ("All " + $failed.Count + " re-run test(s) passed on retry; the suite is green.")
exit 0
