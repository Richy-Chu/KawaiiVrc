<#
.SYNOPSIS
    Builds a Velopack release for KawaiiVrc and publishes it to GitHub Releases.

.DESCRIPTION
    Velopack updates only work for apps installed by a Velopack Setup.exe. This script
    publishes the app, packs the release assets (Setup, full/delta nupkg, releases.win.json)
    and uploads them to a GitHub release, which is the feed the in-app updater reads.

    Three steps run in order: dotnet publish into artifacts/publish, vpk pack into
    artifacts/releases, then gh release upload. A cancel anywhere after the build leaves
    only local files behind.

    Requirements:
      * .NET 8 SDK
      * vpk            -> dotnet tool install -g vpk
      * GitHub CLI     -> https://cli.github.com

.EXAMPLE
    pwsh -File build/pack-release.ps1 -Version 1.1.0

    Full release: builds, packs a delta against the previous release, and publishes.

.EXAMPLE
    pwsh -File build/pack-release.ps1 -Version 1.1.0 -SkipUpload -SkipFetch

    Local rehearsal: builds and packs into artifacts/releases without touching GitHub.
    Use -SkipUpload for this, not -SkipPublish.

.PARAMETER Version
    Version to build, for example 1.1.0.

.PARAMETER Repo
    GitHub owner/name to publish to. Defaults to the update repository compiled into the app.

.PARAMETER NotesFile
    Markdown file with release notes, uploaded with the release.

.PARAMETER Configuration
    Build configuration passed to dotnet publish. Defaults to Release.

.PARAMETER Runtime
    Target runtime identifier. Defaults to win-x64.

.PARAMETER HistoryDepth
    How many previous releases to download as delta bases. Defaults to 1 (the previous
    release), which is what the new delta needs.

.PARAMETER FrameworkDependent
    Publishes framework-dependent instead of self-contained.

.PARAMETER SkipPublish
    Skips the dotnet publish step only. vpk pack reads artifacts/publish, so this fails
    unless that folder already holds a build. For a local rehearsal use -SkipUpload instead.

.PARAMETER SkipUpload
    Builds and packs but uploads nothing, so GitHub releases and assets stay untouched.
    This is the flag for a local rehearsal.

.PARAMETER SkipFetch
    Skips downloading previous releases, so the new release is built without a delta.

.NOTES
    The repository comes from <AssemblyMetadata Include="UpdateRepository" ...> in
    VrcCompanion.csproj, so the updater and this script always agree.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Repo,
    [string]$NotesFile,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [int]$HistoryDepth = 1,
    [switch]$FrameworkDependent,
    [switch]$SkipPublish,
    [switch]$SkipUpload,
    [switch]$SkipFetch
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "VrcCompanion\VrcCompanion.csproj"
$publishDir = Join-Path $root "artifacts\publish"
$releasesDir = Join-Path $root "artifacts\releases"

# Native tools write progress and warnings to stderr. Under ErrorActionPreference = Stop,
# Windows PowerShell turns that into a terminating error, so run them under Continue and
# decide from the exit code instead.
function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$Exe,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $Exe @Arguments 2>&1 | ForEach-Object { Write-Host $_ }
        return $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

function Invoke-NativeCapture {
    param(
        [Parameter(Mandatory = $true)][string]$Exe,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = @(& $Exe @Arguments 2>&1)
        return [pscustomobject]@{ Output = $output; ExitCode = $LASTEXITCODE }
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

function Get-CsprojValue([string]$Pattern) {
    $match = Select-String -Path $project -Pattern $Pattern | Select-Object -First 1
    if ($null -eq $match) { return $null }
    return $match.Matches[0].Groups[1].Value
}

if (-not $Version) {
    $Version = Get-CsprojValue '<Version>([^<]+)</Version>'
}

if (-not $Version) {
    throw "No version given and none found in VrcCompanion.csproj."
}

if ($Version -notmatch '^\d+\.\d+\.\d+([-.+].*)?$') {
    throw "Version '$Version' is not semver2. Velopack rejects four-part versions such as 1.0.0.0."
}

if (-not $Repo) {
    $feed = Get-CsprojValue 'UpdateRepository" Value="https://github\.com/([^"]+)"'
    if ($feed) { $Repo = $feed.TrimEnd('/') }
}

if (-not $Repo) {
    throw "No repository given. Pass -Repo owner/name."
}

Write-Host "KawaiiVrc $Version  ->  $Repo" -ForegroundColor Cyan

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    $localVpk = Join-Path $env:USERPROFILE ".dotnet\tools\vpk.exe"
    if (Test-Path $localVpk) {
        $env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"
    }
    else {
        throw "vpk is missing. Install it with: dotnet tool install -g vpk"
    }
}

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $releasesDir -Force | Out-Null

if (-not $SkipPublish) {
    $selfContained = if ($FrameworkDependent) { "false" } else { "true" }
    Write-Host "Publishing ($Runtime, self-contained=$selfContained)..." -ForegroundColor Cyan
    $publishArgs = @(
        "publish", $project,
        "-c", $Configuration,
        "-r", $Runtime,
        "--self-contained", $selfContained,
        "-p:Version=$Version",
        "-p:InformationalVersion=$Version",
        "-o", $publishDir
    )
    $publishExit = Invoke-Native "dotnet" $publishArgs
    if ($publishExit -ne 0) { throw "dotnet publish failed with exit code $publishExit." }
}

# vpk needs the packages it builds deltas against, so pull the previous release in
# before packing. Chaining across several skipped versions works because every
# release's feed lists its own delta and adds it to the merged feed the client reads.
if (-not $SkipFetch) {
    Write-Host "Fetching the last $HistoryDepth releases for delta updates..." -ForegroundColor Cyan
    $releaseList = Invoke-NativeCapture "gh" @("release", "list", "--repo", $Repo, "--limit", $HistoryDepth, "--json", "tagName", "--jq", ".[].tagName")
    if ($releaseList.ExitCode -ne 0 -or -not $releaseList.Output) {
        Write-Host "  No previous releases found; this will be a full-only release." -ForegroundColor DarkGray
    }
    else {
        foreach ($tag in $releaseList.Output) {
            $tag = "$tag".Trim()
            if ($tag -notmatch '^v?\d') { continue }
            $downloadExit = Invoke-Native "gh" @("release", "download", $tag, "--repo", $Repo, "--pattern", "*.nupkg", "--dir", $releasesDir, "--skip-existing")
            if ($downloadExit -ne 0) {
                Write-Host "  Could not fetch $tag packages; older versions will get the full download." -ForegroundColor DarkGray
            }
        }
    }
}

$packArgs = @(
    "pack",
    "--packId", "KawaiiVrc",
    "--packVersion", $Version,
    "--packDir", $publishDir,
    "--mainExe", "KawaiiVrc.exe",
    "--packTitle", "KawaiiVrc",
    "--packAuthors", "KawaiiVrc",
    "--outputDir", $releasesDir,
    "--runtime", $Runtime
)

$icon = Join-Path $root "VrcCompanion\Assets\app-icon.ico"
if (Test-Path $icon) { $packArgs += @("--icon", $icon) }

if ($NotesFile) {
    if (-not (Test-Path $NotesFile)) { throw "Notes file not found: $NotesFile" }
    $packArgs += @("--releaseNotes", (Resolve-Path $NotesFile).Path)
}

Write-Host "Packing..." -ForegroundColor Cyan
$packExit = Invoke-Native "vpk" $packArgs
if ($packExit -ne 0) { throw "vpk pack failed with exit code $packExit." }

# vpk writes every package in the output directory into the feed (old fulls included).
# Velopack merges the feeds of the last ten releases when checking for updates, so a
# package listed by several releases is downloaded several times at once and the
# parallel patcher races on the same file. Keep each release's feed to its own version.
$feedPath = Join-Path $releasesDir "releases.win.json"
$feed = Get-Content $feedPath -Raw | ConvertFrom-Json
$feed.Assets = @($feed.Assets | Where-Object { $_.Version -eq $Version })
$feed | ConvertTo-Json -Depth 6 -Compress | Set-Content $feedPath -NoNewline
Write-Host "Feed trimmed to $Version only." -ForegroundColor DarkGray

$assets = Get-ChildItem $releasesDir -File | Where-Object {
    ($_.Extension -in @(".exe", ".nupkg", ".json", ".zip") -or $_.Name -eq "RELEASES") -and
    # Older packages are only delta inputs, and Velopack resolves each release's own
    # packages from that release, so only this version's nupkgs need uploading.
    -not ($_.Name -like "*.nupkg" -and $_.Name -notlike "*$Version*")
}

if (-not $assets) { throw "vpk produced no release assets in $releasesDir." }

Write-Host "Release assets:" -ForegroundColor Cyan
$assets | ForEach-Object { Write-Host ("  " + $_.Name) }

if ($SkipUpload) {
    Write-Host "Skipping upload (-SkipUpload)." -ForegroundColor Yellow
    return
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI (gh) is required to publish. Re-run with -SkipUpload to pack only."
}

$tag = "v$Version"
$viewExit = Invoke-Native "gh" @("release", "view", $tag, "--repo", $Repo)
if ($viewExit -eq 0) {
    throw "Release $tag already exists on $Repo. Bump the version instead of reusing a tag."
}

$releaseArgs = @(
    "release", "create", $tag,
    "--repo", $Repo,
    "--title", "KawaiiVrc $Version",
    "--latest"
)

if ($NotesFile) {
    $releaseArgs += @("--notes-file", (Resolve-Path $NotesFile).Path)
}
else {
    $releaseArgs += @("--generate-notes")
}

$releaseArgs += @($assets | ForEach-Object { $_.FullName })

Write-Host "Publishing $tag..." -ForegroundColor Cyan
$releaseExit = Invoke-Native "gh" $releaseArgs
if ($releaseExit -ne 0) { throw "gh release create failed with exit code $releaseExit." }

Write-Host ""
Write-Host "Published $tag to $Repo." -ForegroundColor Green
Write-Host "Anyone on an installed build will see the update popup within a few minutes." -ForegroundColor Green
Write-Host "Reminder: copies installed before the first Velopack release must run the Setup.exe in $releasesDir once by hand." -ForegroundColor Yellow
