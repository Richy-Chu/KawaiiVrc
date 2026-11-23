# Re-runs a run that failed, once, and reports what passed on the second try. A flaky test must not
# redden a commit that is otherwise good, but it must not be hidden either: the names are reported
# as warnings, and the job stays green only when every one of them ran again and passed.
#
# A hung test aborts the whole run and the run keeps no results at all, so it cannot be retried by
# name: the second run repeats the whole suite. A failure that is broad rather than flaky is not
# retried at all, because re-running it would turn a real break green.
param(
    # The .trx the first run wrote.
    [string] $Results = "",

    [string] $Project = "VrcCompanion.Tests/VrcCompanion.Tests.csproj",

    [string] $Configuration = "Release",

    # How long a single test may make no progress before the run is aborted as hung. It bounds the
    # first run and every re-run this script starts, so a hang can never run out the job's clock.
    [string] $HangTimeout = "2m",

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
                Full = [string] $_.testName
                Passed = $_.outcome -eq "Passed"
                Ran = $_.outcome -ne "NotExecuted"
            }
        } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_.Name) })
}

function Read-Summary([string] $path) {
    [xml] $trx = Get-Content -LiteralPath $path -Raw
    $total = 0
    if ($null -ne $trx.TestRun.ResultSummary.Counters) {
        $total = [int] $trx.TestRun.ResultSummary.Counters.total
    }

    return $total
}

function Read-AbortReason([string] $path) {
    # A finished run writes outcome="Failed" when tests failed, so the summary outcome cannot tell
    # an abort from a red suite. The abort message is the signal: only a run the host killed says
    # it was aborted, and everything it had recorded went with it.
    [xml] $trx = Get-Content -LiteralPath $path -Raw
    $reason = @($trx.TestRun.ResultSummary.RunInfos.RunInfo |
        ForEach-Object { [string] $_.Text } |
        Where-Object { $_ -match "was aborted" }) | Select-Object -First 1

    if ($null -eq $reason) {
        return $null
    }

    return $reason.Trim()
}

function Read-Sequence([string] $directory) {
    # The blame collector's sequence file names the tests that were still running when the host was
    # killed. It is the only record an aborted run leaves: the logger never flushes a result.
    $names = @()
    $files = @(Get-ChildItem -LiteralPath $directory -Filter "Sequence_*.xml" -File -Recurse -Force -ErrorAction SilentlyContinue)
    foreach ($file in $files) {
        try {
            [xml] $sequence = Get-Content -LiteralPath $file.FullName -Raw
            $names += @($sequence.TestSequence.Test |
                Where-Object { [string] $_.Completed -eq "False" } |
                ForEach-Object { (([string] $_.Name) -split "\(")[0] })
        }
        catch {
            # An unreadable sequence names nothing; the run is still aborted, and that fails below.
            Write-Verbose ("Unreadable sequence " + $file.FullName + ": " + $_.Exception.Message)
        }
    }

    return @($names | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
}

function Invoke-Suite {
    param(
        [string] $Project,
        [string] $Configuration,
        [string] $HangTimeout,
        [string] $Directory,
        [string] $Log,
        [string[]] $Filter
    )

    # The hang bound is on every run this script starts: a retry of a hung test must not be able
    # to hang the job either.
    $arguments = @(
        "test", $Project, "-c", $Configuration, "--no-build", "--nologo",
        "--blame-hang", "--blame-hang-timeout", $HangTimeout, "--blame-hang-dump-type", "none",
        "--logger", ("trx;LogFileName=" + $Log),
        "--results-directory", $Directory)
    if ($null -ne $Filter) {
        $arguments += @("--filter", ($Filter -join "|"))
    }

    # Out-Host, not the pipeline: the caller assigns this function's output, and dotnet's console
    # text in that assignment would turn its exit code into an array of strings that no -eq reads
    # as zero.
    & dotnet @arguments | Out-Host
    return $LASTEXITCODE
}

if (-not (Test-Path -LiteralPath $Results -PathType Leaf)) {
    throw ("No test results at " + $Results + ". The first run did not get far enough to fail a test, so there is nothing to retry: look at the step that ran it.")
}

$directory = Split-Path -Parent $Results
$aborted = Read-AbortReason $Results

if ($null -ne $aborted) {
    # A hung test killed the host: the run was aborted and recorded nothing, so there is no failed
    # set to retry. Re-running the whole suite is the only honest retry, because everything the
    # aborted run had not reached is unverified.
    $hung = @(Read-Sequence $directory)
    if ($hung.Count -eq 0) {
        throw ("The run was aborted (" + $aborted + ") with " + (Read-Summary $Results) + " result(s) and no blame sequence naming the test that was running, so the hang cannot be placed. See " + $directory + ".")
    }

    Write-Host ("The run was aborted by a hang in " + ($hung -join ", ") + "; it recorded " + (Read-Summary $Results) + " result(s).")
    Write-Host "An aborted run keeps no results, so every test it had not reached is unverified: the whole suite is re-run."

    if ($PlanOnly) {
        Write-Host "Filter: (all tests)"
        exit 0
    }

    $retried = Join-Path $directory "retry.trx"
    if (Test-Path -LiteralPath $retried -PathType Leaf) {
        Remove-Item -LiteralPath $retried -Force
    }

    $code = Invoke-Suite -Project $Project -Configuration $Configuration -HangTimeout $HangTimeout `
        -Directory $directory -Log "retry.trx" -Filter $null

    if ($code -eq 0) {
        foreach ($name in $hung) {
            Write-Host ("::warning title=Passed on retry::" + $name + " (hung once; the whole suite re-ran)")
        }

        if ($env:GITHUB_STEP_SUMMARY) {
            $summaryText = "### Passed on retry after a hang`n`n" +
                (($hung | ForEach-Object { "- ``$_``" }) -join "`n") +
                "`n`nThe hang aborted the first run and discarded its results, so the whole suite was re-run, not just this test.`n" +
                "The run's ``.trx`` and blame sequence files are attached to this run as an artifact.`n"
            Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summaryText
        }

        Write-Host "The suite passed when re-run; it is green."
        exit 0
    }

    $again = @()
    if (Test-Path -LiteralPath $retried -PathType Leaf) {
        if ($null -ne (Read-AbortReason $retried)) {
            $again = @(Read-Sequence $directory | Where-Object { $hung -notcontains $_ })
        }
        else {
            $again = @(Read-Result $retried | Where-Object { $_.Ran -and -not $_.Passed } | ForEach-Object { $_.Name } | Sort-Object -Unique)
        }
    }

    if ($again.Count -gt 0) {
        Write-Host ("Still failing on retry: " + ($again -join ", "))
    }

    Write-Host "The re-run did not save the suite, so it is red."
    exit 1
}

$cases = @(Read-Result $Results | Where-Object { $_.Ran -and -not $_.Passed })
$failed = @($cases | ForEach-Object { $_.Name } | Sort-Object -Unique)
if ($cases.Count -eq 0) {
    throw ("The first run failed but " + $Results + " names no failed test, so the failure was not a test failure.")
}

# A handful of test cases can flake; a broad one cannot. Cases, not method names: a data-driven
# test that fails most of its rows is a broken theory, not one flake, however few names it makes.
# Re-running a break that broad would turn it green and ship it, so above the limit the run is
# the commit's, not the runner's.
$maxFlakes = 5
if ($cases.Count -gt $maxFlakes) {
    $listed = (($cases | Select-Object -First $maxFlakes | ForEach-Object { $_.Full }) -join ", ")
    if ($cases.Count -gt $maxFlakes) {
        $listed += ", ..."
    }

    Write-Host ("The first run failed " + $cases.Count + " test case(s) across " + $failed.Count +
        " method(s): " + $listed)
    Write-Host ("At most " + $maxFlakes + " can pass for a flake, so this looks systemic, not flaky. Nothing was re-run.")
    exit 1
}

Write-Host ("Retrying " + $failed.Count + " failed test(s): " + ($failed -join ", "))

if ($PlanOnly) {
    Write-Host ("Filter: " + (($failed | ForEach-Object { "FullyQualifiedName~" + $_ }) -join "|"))
    exit 0
}

$retried = Join-Path $directory "retry.trx"
if (Test-Path -LiteralPath $retried -PathType Leaf) {
    Remove-Item -LiteralPath $retried -Force
}

$code = Invoke-Suite -Project $Project -Configuration $Configuration -HangTimeout $HangTimeout `
    -Directory $directory -Log "retry.trx" -Filter $failed

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
    $summaryText = "### Passed on retry`n`n" + (($failed | ForEach-Object { "- ``$_``" }) -join "`n") +
        "`n`nThe first run's and the retry's ``.trx`` files are attached to this run as an artifact.`n"
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summaryText
}

Write-Host ("All " + $failed.Count + " re-run test(s) passed on retry; the suite is green.")
exit 0
