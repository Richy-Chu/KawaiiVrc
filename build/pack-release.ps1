<#
.SYNOPSIS
    Builds a Velopack release for KawaiiVrc and (optionally) publishes it to GitHub Releases.

.DESCRIPTION
    Velopack updates only work for apps installed by a Velopack Setup.exe. This script
    publishes the app, packs the release assets (Setup, full/delta nupkg, releases.win.json)
    and uploads them to a GitHub release, which is the feed the in-app updater reads.

    Run it from anywhere:

        pwsh -File build/pack-release.ps1 -Version 1.1.0 -NotesFile notes.md

    Requirements:
      * .NET 8 SDK
      * vpk            -> dotnet tool install -g vpk
      * GitHub CLI     -> https://cli.github.com  (for -NoUpload use -SkipUpload)

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
    [switch]$FrameworkDependent,
    [switch]$SkipPublish,
    [switch]$SkipUpload
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "VrcCompanion\VrcCompanion.csproj"
$publishDir = Join-Path $root "artifacts\publish"
$releasesDir = Join-Path $root "artifacts\releases"

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
    dotnet publish $project `
        -c $Configuration `
        -r $Runtime `
        --self-contained $selfContained `
        -p:Version=$Version `
        -p:InformationalVersion=$Version `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
}

# Velopack only builds delta packages when it can see the previous release's nupkg,
# so pull the last release's packages into the output folder first.
if (-not $SkipUpload -and (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Host "Fetching the previous release packages for delta updates..." -ForegroundColor Cyan
    gh release download --repo $Repo --pattern "*.nupkg" --dir $releasesDir 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  No previous release found; this will be a full-only release." -ForegroundColor DarkGray
        $global:LASTEXITCODE = 0
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
vpk @packArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed." }

$assets = Get-ChildItem $releasesDir -File | Where-Object {
    $_.Extension -in @(".exe", ".nupkg", ".json", ".zip") -or $_.Name -eq "RELEASES"
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
gh release view $tag --repo $Repo *> $null
if ($LASTEXITCODE -eq 0) {
    throw "Release $tag already exists on $Repo. Bump the version instead of reusing a tag."
}

$notes = if ($NotesFile) { (Resolve-Path $NotesFile).Path } else { $null }
$releaseArgs = @(
    "release", "create", $tag,
    "--repo", $Repo,
    "--title", "KawaiiVrc $Version",
    "--latest"
)
if ($notes) { $releaseArgs += @("--notes-file", $notes) } else { $releaseArgs += @("--generate-notes") }

Write-Host "Publishing $tag..." -ForegroundColor Cyan
$releaseArgs += @($assets | ForEach-Object { $_.FullName })
gh @releaseArgs
if ($LASTEXITCODE -ne 0) { throw "gh release create failed." }

Write-Host ""
Write-Host "Published $tag to $Repo." -ForegroundColor Green
Write-Host "Anyone on an installed build will see the update popup within a few minutes." -ForegroundColor Green
Write-Host "Reminder: copies installed before the first Velopack release must run the Setup.exe in $releasesDir once by hand." -ForegroundColor Yellow
