# Opens or updates the "Release verification is failing" issue, and closes it once the
# check passes again. Values come from the workflow step's env: block.
$ErrorActionPreference = "Stop"

$repo = $env:REPO
$runUrl = $env:RUN_URL
$outcome = $env:OUTCOME
$label = "release-verification"

if (-not $repo) { throw "REPO is not set." }
if (-not $runUrl) { throw "RUN_URL is not set." }

# Idempotent: creates the label on the first run and leaves it alone after that.
gh label create $label --repo $repo --description "Automated release feed and delta chain checks" --color "BFD4F2" --force | Out-Null

$open = @(gh issue list --repo $repo --label $label --state open --json number --jq ".[0].number") -join ""
$open = $open.Trim()

if ($outcome -eq "failure") {
    $body = @(
        "The release verification failed: $runUrl",
        "",
        "It checks that every release's nupkgs match its own feed and that an install two releases back still reaches the latest release through delta patches only.",
        "",
        "Fix the release assets, then re-run it from the Actions tab: Release verification -> Run workflow."
    ) -join "`n"

    if ($open) {
        gh issue comment $open --repo $repo --body "Still failing: $runUrl"
        Write-Host "Commented on issue #$open."
    }
    else {
        $created = gh issue create --repo $repo --title "Release verification is failing" --label $label --body $body
        Write-Host "Opened issue $created."
    }
}
elseif ($open) {
    gh issue close $open --repo $repo --comment "Passing again: $runUrl"
    Write-Host "Closed issue #$open."
}
else {
    Write-Host "Verification passed and no open drift issue."
}
