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

.PARAMETER VerifyFrom
    Release to update from when checking the delta chain. Defaults to the release two
    versions back, so the check always covers a skipped version.

.PARAMETER SkipVerify
    Skips the post-publish checks: feed/asset parity and the delta chain proof.

.PARAMETER VerifyOnly
    Runs only the post-publish checks against the published releases, without building,
    packing or uploading. Useful after the fact or for a scheduled check.

.PARAMETER AllowDirty
    Publishes even though the working tree has uncommitted changes.

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
    [switch]$SkipFetch,
    [string]$VerifyFrom,
    [switch]$SkipVerify,
    [switch]$VerifyOnly,
    [switch]$AllowDirty
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot

# Version ordering must handle prereleases (1.0.0-beta), which [version] rejects.
. (Join-Path $PSScriptRoot "semver.ps1")

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

# A published release is a promise: the tag names a commit that contains the built code,
# and the feed it serves can be resolved. Check both before anything is uploaded.
function Test-ReleaseReadiness {
    param([switch]$AllowDirty)

    $projectVersion = Get-CsprojValue '<Version>([^<]+)</Version>'
    if ($projectVersion -ne $Version) {
        throw "VrcCompanion.csproj is at $projectVersion but the release is $Version. Bump <Version> first so the tag matches the build."
    }

    if ($AllowDirty) {
        Write-Host "  Working tree check skipped (-AllowDirty)." -ForegroundColor Yellow
        return
    }

    $status = Invoke-NativeCapture "git" @("status", "--porcelain")
    $dirty = @($status.Output | Where-Object { "$_".Trim() })
    if ($status.ExitCode -ne 0) {
        throw "git status failed; cannot confirm the tree is clean."
    }

    if ($dirty.Count -gt 0) {
        throw ("Working tree has uncommitted changes; commit or stash before publishing:" +
            [Environment]::NewLine + ($dirty -join [Environment]::NewLine) +
            [Environment]::NewLine + "Use -AllowDirty to override.")
    }

    $upstream = Invoke-NativeCapture "git" @("rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}")
    if ($upstream.ExitCode -ne 0) {
        Write-Host "  No upstream branch; skipping the push check." -ForegroundColor DarkGray
        return
    }

    $ahead = Invoke-NativeCapture "git" @("rev-list", "--count", "@{u}..HEAD")
    $count = "$($ahead.Output)".Trim()
    if ($ahead.ExitCode -ne 0 -or $count -ne "0") {
        throw "HEAD has $count commit(s) that are not pushed; the release would tag code GitHub does not have."
    }
}

# Velopack merges the feeds of the last ten releases, so a release whose assets are not
# exactly what its own feed lists breaks chaining for everyone who skipped a version.
function Test-ReleaseFeedParity {
    $list = Invoke-NativeCapture "gh" @("release", "list", "--repo", $Repo, "--limit", "20", "--json", "tagName", "--jq", ".[].tagName")
    if ($list.ExitCode -ne 0) {
        # Not being able to read the releases says nothing about them.
        Write-Host "Could not list releases on $Repo (gh exit $($list.ExitCode)); this is not release drift." -ForegroundColor Yellow
        exit 3
    }

    if (-not $list.Output) {
        Write-Host "  No releases to compare against their feeds." -ForegroundColor DarkGray
        return
    }

    $failures = New-Object System.Collections.Generic.List[string]
    $scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("kawaiivrc-feed-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null
    try {
        foreach ($tag in $list.Output) {
            $tag = "$tag".Trim()
            if (-not $tag) { continue }

            $assetOutput = Invoke-NativeCapture "gh" @("release", "view", $tag, "--repo", $Repo, "--json", "assets", "--jq", ".assets[].name")
            if ($assetOutput.ExitCode -ne 0) {
                Write-Host "Could not read the assets of $tag (gh exit $($assetOutput.ExitCode)); this is not release drift." -ForegroundColor Yellow
                exit 3
            }

            $names = @($assetOutput.Output | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
            $assets = @($names | Where-Object { $_ -like "*.nupkg" } | Sort-Object)

            # Whether the feed exists is decided by the asset list, not by how a failed
            # download is worded: a listed feed that will not download is a transport problem.
            if ($names -notcontains "releases.win.json") {
                Write-Host "  $tag has no feed asset." -ForegroundColor Yellow
                $failures.Add("$tag : releases.win.json is missing")
                continue
            }

            $download = Invoke-NativeCapture "gh" @("release", "download", $tag, "--repo", $Repo, "--pattern", "releases.win.json", "--dir", $scratch, "--clobber")
            if ($download.ExitCode -ne 0) {
                Write-Host "Could not download the feed of $tag (gh exit $($download.ExitCode)); this is not release drift." -ForegroundColor Yellow
                exit 3
            }

            $feed = Get-Content (Join-Path $scratch "releases.win.json") -Raw | ConvertFrom-Json
            $feedNames = @($feed.Assets | ForEach-Object { $_.FileName } | Where-Object { $_ -like "*.nupkg" } | Sort-Object)

            $diff = Compare-Object -ReferenceObject $feedNames -DifferenceObject $assets
            if ($diff) {
                $missing = ($diff | Where-Object { $_.SideIndicator -eq "<=" } | ForEach-Object { $_.InputObject }) -join ", "
                $extra = ($diff | Where-Object { $_.SideIndicator -eq "=>" } | ForEach-Object { $_.InputObject }) -join ", "
                $failures.Add("$tag : feed lists [$missing], release also hosts [$extra]")
            }
            else {
                Write-Host "  $tag : $(@($feedNames).Count) nupkg(s) match the feed." -ForegroundColor DarkGray
            }
        }
    }
    finally {
        Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
    }

    if ($failures.Count -gt 0) {
        throw ("Release assets do not match their feeds:" + [Environment]::NewLine + ($failures -join [Environment]::NewLine))
    }
}

function Invoke-ChainVerification {
    param(
        [Parameter(Mandatory = $true)][string]$TargetVersion,
        [string]$VerifyFrom
    )

    $verifier = Join-Path $PSScriptRoot "ReleaseVerifier\ReleaseVerifier.csproj"
    if (-not (Test-Path $verifier)) {
        Write-Host "build/ReleaseVerifier/ReleaseVerifier.csproj is missing, so the check cannot run. That is not release drift." -ForegroundColor Yellow
        exit 3
    }

    $list = Invoke-NativeCapture "gh" @("release", "list", "--repo", $Repo, "--limit", "20", "--json", "tagName", "--jq", ".[].tagName")
    if ($list.ExitCode -ne 0) {
        # Without the list there is no chain to check, which is not release drift.
        Write-Host "Could not list releases for the chain check on $Repo (gh exit $($list.ExitCode)); this is not release drift." -ForegroundColor Yellow
        exit 3
    }

    $versions = @($list.Output |
        ForEach-Object { "$_".Trim().TrimStart("v") } |
        Where-Object { $_ -match '^\d+\.\d+\.\d+(-[0-9A-Za-z.\-]+)?(\+[0-9A-Za-z.\-]+)?$' })
    $versions = @(Get-SortedSemver $versions)

    $prior = @($versions | Where-Object { (Compare-Semver $_ $TargetVersion) -lt 0 })
    $baseVersion = if ($VerifyFrom) { $VerifyFrom.TrimStart("v") } else { $null }
    if ($baseVersion) {
        if ($baseVersion -notin $versions) { throw "-VerifyFrom $baseVersion is not a published release." }
    }
    elseif ($prior.Count -ge 2) {
        # Two releases back guarantees the check covers chaining across a skipped version.
        $baseVersion = $prior[1]
    }
    elseif ($prior.Count -eq 1) {
        $baseVersion = $prior[0]
    }
    else {
        Write-Host "  No older release to update from; skipping the chain check." -ForegroundColor DarkGray
        return
    }

    if ((Compare-Semver $baseVersion $TargetVersion) -ge 0) {
        throw "-VerifyFrom $baseVersion must be older than $TargetVersion."
    }

    $expected = @($versions | Where-Object {
        (Compare-Semver $_ $baseVersion) -gt 0 -and (Compare-Semver $_ $TargetVersion) -le 0
    }).Count

    # Build separately from the run: a compile error means the check never ran, and dotnet
    # would otherwise report it with the same exit code as a failed verification.
    $buildExit = Invoke-Native "dotnet" @("build", $verifier, "-c", "Release", "--nologo", "-v", "quiet")
    if ($buildExit -ne 0) {
        Write-Host "build/ReleaseVerifier could not be built (exit $buildExit), so the check never ran. That is not release drift." -ForegroundColor Yellow
        exit 3
    }

    $verifierDll = @(Get-ChildItem (Join-Path $PSScriptRoot "ReleaseVerifier\bin\Release\*\ReleaseVerifier.dll") -File | Select-Object -First 1)
    if (-not $verifierDll) {
        Write-Host "ReleaseVerifier.dll was not produced by the build, so the check never ran. That is not release drift." -ForegroundColor Yellow
        exit 3
    }

    Write-Host "Verifying $baseVersion -> $TargetVersion through $expected delta(s) on $Repo..." -ForegroundColor Cyan
    $verifyExit = Invoke-Native "dotnet" @(
        $verifierDll.FullName,
        "--repo", $Repo, "--from", $baseVersion, "--target", $TargetVersion, "--expect", "$expected")

    # Exit code 3 means GitHub refused or never answered, which is not release drift and
    # must not be reported as such. The caller turns this into an infrastructure failure.
    if ($verifyExit -eq 3) {
        Write-Host "The chain check could not reach GitHub (rate limit, auth or network); this is not release drift." -ForegroundColor Yellow
        exit 3
    }

    if ($verifyExit -ne 0) {
        throw "Delta chain verification failed: $baseVersion -> $TargetVersion."
    }

    Write-Host "Chain verified: $baseVersion -> $TargetVersion applies deltas only." -ForegroundColor Green
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

if ($VerifyOnly) {
    Test-ReleaseFeedParity
    Invoke-ChainVerification -TargetVersion $Version -VerifyFrom $VerifyFrom
    return
}

if (-not $SkipUpload) {
    Test-ReleaseReadiness -AllowDirty:$AllowDirty
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
    if ($releaseList.ExitCode -ne 0) {
        # A failed listing is not an empty one: packing anyway silently ships a full-only
        # release where a delta was expected.
        throw ("Could not list previous releases on $Repo (gh exit $($releaseList.ExitCode)); " +
            "refusing to build a release without its delta base. Re-run when GitHub is reachable, " +
            "or pass -SkipFetch to accept a full-only release.")
    }

    if (-not $releaseList.Output) {
        Write-Host "  No previous releases found; this will be a full-only release." -ForegroundColor DarkGray
    }
    else {
        foreach ($tag in $releaseList.Output) {
            $tag = "$tag".Trim()
            if ($tag -notmatch '^v?\d') { continue }
            $downloadExit = Invoke-Native "gh" @("release", "download", $tag, "--repo", $Repo, "--pattern", "*.nupkg", "--dir", $releasesDir, "--skip-existing")
            if ($downloadExit -ne 0) {
                # A partial base packs a delta against packages that are not all there.
                throw ("Could not download the packages of $tag from $Repo (gh exit $downloadExit); " +
                    "refusing to build a delta from an incomplete base. Re-run when GitHub is reachable, " +
                    "or pass -SkipFetch to accept a full-only release.")
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
$view = Invoke-NativeCapture "gh" @("release", "view", $tag, "--repo", $Repo)
if ($view.ExitCode -eq 0) {
    throw "Release $tag already exists on $Repo. Bump the version instead of reusing a tag."
}

# Only "not found" means the tag is free. A rate limit or auth error reads the same way to
# a bare exit-code check, which would send the release on to gh release create.
$viewText = (($view.Output | ForEach-Object { "$_" }) -join " ") -replace '\s+', ' '
if ($view.ExitCode -ne 1 -or $viewText -notmatch '(?i)not found') {
    throw "Could not check whether $tag exists on $Repo (gh exit $($view.ExitCode)): $viewText"
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

if (-not $SkipVerify) {
    Test-ReleaseFeedParity
    Invoke-ChainVerification -TargetVersion $Version -VerifyFrom $VerifyFrom
}

Write-Host "Anyone on an installed build will see the update popup within a few minutes." -ForegroundColor Green
Write-Host "Reminder: copies installed before the first Velopack release must run the Setup.exe in $releasesDir once by hand." -ForegroundColor Yellow
