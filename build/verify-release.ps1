# Resolves which published release to verify, then runs the verification-only mode of
# pack-release.ps1. The workflow passes REPO and the optional inputs through its env: block.
$ErrorActionPreference = "Stop"

$repo = $env:REPO
$version = "$($env:INPUT_VERSION)".Trim()
$from = "$($env:INPUT_FROM)".Trim()

if (-not $repo) { throw "REPO is not set." }

# The project version can be ahead of the published releases while a release is being
# prepared, so default to what is actually on GitHub.
if (-not $version) {
    $version = @(gh release list --repo $repo --limit 1 --json tagName --jq ".[].tagName") -join ""
    $version = $version.Trim() -replace "^v", ""
}
if (-not $version) { throw "No release to verify on $repo." }
Write-Host "Verifying release $version."

$arguments = @(
    "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $PSScriptRoot "pack-release.ps1"),
    "-VerifyOnly", "-Version", $version
)
if ($from) { $arguments += @("-VerifyFrom", $from) }

pwsh @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
