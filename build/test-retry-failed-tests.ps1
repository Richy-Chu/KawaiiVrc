# Checks the retry plan without running dotnet: which tests it picks from a results file, that a
# theory's data does not leak into the filter, and that a results file with no failure is refused
# rather than reported as an empty success.
$ErrorActionPreference = "Stop"

$retry = Join-Path $PSScriptRoot "retry-failed-tests.ps1"
$shell = (Get-Process -Id $PID).Path
$root = Join-Path ([System.IO.Path]::GetTempPath()) ("kawaiivrc-retry-check-" + [guid]::NewGuid().ToString("N"))

function Write-Trx([string] $path, [string[]] $results) {
    $body = $results -join "`n    "
    [System.IO.File]::WriteAllText($path, @"
<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    $body
  </Results>
</TestRun>
"@)
}

function Invoke-Plan([string] $path) {
    # A refusal writes its reason to stderr, and this script stops on errors, so the run's own
    # stderr must not be promoted to a terminating error before its exit code is read.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = @(& $shell -NoProfile -File $retry -Results $path -PlanOnly 2>&1)
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Text = ($output -join "`n") }
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

New-Item -ItemType Directory -Path $root -Force | Out-Null
try {
    $failures = 0

    # One passing, one failing, two cases of the same failing theory, one skipped.
    $mixed = Join-Path $root "mixed.trx"
    Write-Trx $mixed @(
        '<UnitTestResult testName="VrcCompanion.Tests.FooTests.First" outcome="Passed" />',
        '<UnitTestResult testName="VrcCompanion.Tests.FooTests.Second" outcome="Failed" />',
        '<UnitTestResult testName="VrcCompanion.Tests.FooTests.Theory(x: 1)" outcome="Failed" />',
        '<UnitTestResult testName="VrcCompanion.Tests.FooTests.Theory(x: 2)" outcome="Failed" />',
        '<UnitTestResult testName="VrcCompanion.Tests.FooTests.Skipped" outcome="NotExecuted" />')

    $plan = Invoke-Plan $mixed
    foreach ($expected in @("VrcCompanion.Tests.FooTests.Second", "VrcCompanion.Tests.FooTests.Theory")) {
        if ($plan.ExitCode -ne 0 -or $plan.Text -notlike "*$expected*") {
            Write-Host ("the plan did not name $expected (exit $($plan.ExitCode)):")
            Write-Host $plan.Text
            $failures++
        }
    }

    foreach ($unwanted in @("FooTests.First", "FooTests.Skipped", "Theory(x: 1)")) {
        if ($plan.Text -like "*$unwanted*") {
            Write-Host ("the plan should not have named $unwanted")
            $failures++
        }
    }

    # A results file that records no failure means the run failed for some other reason.
    $green = Join-Path $root "green.trx"
    Write-Trx $green @('<UnitTestResult testName="VrcCompanion.Tests.FooTests.First" outcome="Passed" />')
    if ((Invoke-Plan $green).ExitCode -eq 0) {
        Write-Host "a results file with no failed test should not plan a retry"
        $failures++
    }

    # The runner can write a failed result without a name, and an unnamed filter matches nothing.
    $unnamed = Join-Path $root "unnamed.trx"
    Write-Trx $unnamed @('<UnitTestResult outcome="Failed" />')
    if ((Invoke-Plan $unnamed).ExitCode -eq 0) {
        Write-Host "a failed result without a name should not plan an empty filter"
        $failures++
    }

    # A missing file is a failure to read, not an empty plan.
    if ((Invoke-Plan (Join-Path $root "absent.trx")).ExitCode -eq 0) {
        Write-Host "a missing results file should not plan a retry"
        $failures++
    }

    if ($failures -gt 0) {
        Write-Host "$failures retry-plan check(s) failed."
        exit 1
    }

    Write-Host "5 retry-plan check(s) passed."
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
