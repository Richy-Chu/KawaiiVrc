# Opens or updates the "Release verification is failing" issue, and closes it once the
# check passes again. Values come from the workflow step's env: block.
$ErrorActionPreference = "Stop"

$repo = $env:REPO
$runUrl = $env:RUN_URL
$outcome = $env:OUTCOME
$label = "release-verification"

if (-not $repo) { throw "REPO is not set." }
if (-not $runUrl) { throw "RUN_URL is not set." }

# A failed gh call must not look like an answer. Treating an unreadable issue list as "no
# issue is open" opens a duplicate on failure and reports a false pass on success, and
# swallowing a failed close leaves a resolved issue open while the run goes green.
function Invoke-Gh {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = @(& gh @Arguments 2>&1)
        $exit = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    if ($exit -ne 0) {
        throw "gh $($Arguments -join ' ') failed with exit $exit, so the issue state is unknown."
    }

    return $output
}

# A rate limit or network failure says nothing about the releases, so leave the issue state
# alone and surface it as an infrastructure warning instead.
$failureKind = "$($env:FAILURE_KIND)".Trim()
if ($outcome -eq "failure" -and $failureKind -eq "infrastructure") {
    Write-Host "::warning::Release verification could not reach GitHub (rate limit, auth or network). Not reported as release drift."
    Write-Host "Left the drift issue untouched: $runUrl"
    exit 0
}

# Idempotent: creates the label on the first run and leaves it alone after that.
Invoke-Gh @("label", "create", $label, "--repo", $repo, "--description", "Automated release feed and delta chain checks", "--color", "BFD4F2", "--force") | Out-Null

$open = (@(Invoke-Gh @("issue", "list", "--repo", $repo, "--label", $label, "--state", "open", "--json", "number", "--jq", ".[0].number")) -join "").Trim()

if ($outcome -eq "failure") {
    $body = @(
        "The release verification failed: $runUrl",
        "",
        "It checks that every release's nupkgs match its own feed and that an install two releases back still reaches the latest release through delta patches only.",
        "",
        "Fix the release assets, then re-run it from the Actions tab: Release verification -> Run workflow."
    ) -join "`n"

    if ($open) {
        Invoke-Gh @("issue", "comment", $open, "--repo", $repo, "--body", "Still failing: $runUrl") | Out-Null
        Write-Host "Commented on issue #$open."
    }
    else {
        $created = (@(Invoke-Gh @("issue", "create", "--repo", $repo, "--title", "Release verification is failing", "--label", $label, "--body", $body)) -join " ").Trim()
        Write-Host "Opened issue $created."
    }
}
elseif ($open) {
    Invoke-Gh @("issue", "close", $open, "--repo", $repo, "--comment", "Passing again: $runUrl") | Out-Null
    Write-Host "Closed issue #$open."
}
else {
    Write-Host "Verification passed and no open drift issue."
}
