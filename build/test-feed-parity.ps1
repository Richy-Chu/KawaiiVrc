# Proves the release checks that read GitHub cannot mistake a failed read for an answer, by
# driving the real pack-release.ps1 against a stub gh on PATH: a release without its feed is
# drift, while a feed that will not download, a chain listing that fails, a delta-base listing
# or download that fails, and a tag check that errors are infrastructure or a refused build,
# never a silent skip or a full-only release. A tag that already exists is refused for reuse,
# and -SkipPublish packs the build already in artifacts/publish, so a sentinel left there
# proves the run did not clear it. A stub git makes the readiness checks answer for
# themselves: only git's own "no upstream configured" may skip the push check, and a dirty
# or unreadable tree must stop the release, and a repository gh cannot read is not an
# absent tag.
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$releaseScript = Join-Path $PSScriptRoot "pack-release.ps1"
if (-not (Test-Path $releaseScript)) { throw "pack-release.ps1 is missing." }

# The readiness check compares the project version with the release, so the publish cases use it.
$csproj = Join-Path (Join-Path $repoRoot "VrcCompanion") "VrcCompanion.csproj"
if (-not (Test-Path $csproj)) { throw "VrcCompanion.csproj is missing." }
$versionMatch = Select-String -Path $csproj -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
if (-not $versionMatch) { throw "VrcCompanion.csproj has no <Version>." }
$projectVersion = $versionMatch.Matches[0].Groups[1].Value

$onWindows = [System.IO.Path]::DirectorySeparatorChar -eq '\'
$shimDir = Join-Path ([System.IO.Path]::GetTempPath()) ("kawaiivrc-gh-stub-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $shimDir -Force | Out-Null

$releasesDir = Join-Path (Join-Path $repoRoot "artifacts") "releases"
New-Item -ItemType Directory -Path $releasesDir -Force | Out-Null

# The publish cases run with -SkipPublish against whatever build .NET last put here.
$publishDir = Join-Path (Join-Path $repoRoot "artifacts") "publish"
$sentinel = Join-Path $publishDir "kawaiivrc-skip-publish-sentinel.txt"
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
Set-Content -Path $sentinel -Value "A -SkipPublish run must not delete this file." -Encoding Ascii

function Write-Stub([string]$Path, [string[]]$Lines, [string]$NewLine) {
    ($Lines -join $NewLine) | Set-Content -Path $Path -Encoding Ascii

    if (-not $Path.EndsWith(".cmd", [System.StringComparison]::OrdinalIgnoreCase)) {
        # Set the bit through .NET rather than trusting an external chmod.
        $mode = [System.IO.UnixFileMode]'UserRead, UserWrite, UserExecute, GroupRead, GroupExecute, OtherRead, OtherExecute'
        [System.IO.File]::SetUnixFileMode($Path, $mode)
    }
}

# Line endings are explicit: cmd.exe mis-parses these blocks with LF, and sh will not run a
# script whose first line ends in CRLF. exit /b inside nested if blocks also reports 0 from
# cmd.exe, so the batch halves use flat labels.
function Write-GhStub([string]$Directory, [bool]$Windows) {
    if ($Windows) {
        Write-Stub -Path (Join-Path $Directory "gh.cmd") -NewLine "`r`n" -Lines @(
            '@echo off',
            'if /I "%1 %2"=="release list" goto release_list',
            'if /I "%1 %2"=="release view" goto release_view',
            'if /I "%1 %2"=="release download" goto release_download',
            'if /I "%1 %2"=="repo view" goto repo_view',
            'goto done',
            '',
            ':release_list',
            'if "%GH_MODE%"=="fetch-list-fails" goto fail',
            'if not "%GH_MODE%"=="chain-list-fails" goto list_ok',
            'if not exist "%GH_STATE%" goto list_first',
            'goto fail',
            '',
            ':list_first',
            'type nul > "%GH_STATE%"',
            'exit /b 0',
            '',
            ':list_ok',
            'echo v1.0.0',
            'exit /b 0',
            '',
            ':release_view',
            'if "%GH_MODE%"=="view-error" goto rate_limit',
            'if "%GH_MODE%"=="view-absent" goto absent',
            'if "%GH_MODE%"=="repo-unreadable" goto absent',
            'echo KawaiiVrc-1.0.0-full.nupkg',
            'if not "%GH_MODE%"=="missing-feed" echo releases.win.json',
            'exit /b 0',
            '',
            ':release_download',
            'if "%GH_MODE%"=="feed-download-fails" goto download_fails',
            'if "%GH_MODE%"=="base-download-fails" goto download_fails',
            'exit /b 0',
            '',
            ':download_fails',
            'echo download failed: connection reset 1>&2',
            'exit /b 1',
            '',
            ':rate_limit',
            'echo gh: API rate limit exceeded 1>&2',
            'exit /b 1',
            '',
            ':absent',
            'echo release not found 1>&2',
            'exit /b 1',
            '',
            ':repo_view',
            'if "%GH_MODE%"=="repo-unreadable" goto repo_missing',
            'exit /b 0',
            '',
            ':repo_missing',
            'echo gh: could not resolve to a repository 1>&2',
            'exit /b 1',
            '',
            ':fail',
            'echo gh: could not reach api.github.com 1>&2',
            'exit /b 1',
            '',
            ':done',
            'exit /b 0'
        )
        return
    }

    Write-Stub -Path (Join-Path $Directory "gh") -NewLine "`n" -Lines @(
        '#!/bin/sh',
        'case "$1 $2" in',
        '  "release list")',
        '    if [ "$GH_MODE" = "fetch-list-fails" ]; then echo "gh: could not reach api.github.com" >&2; exit 1; fi',
        '    if [ "$GH_MODE" = "chain-list-fails" ]; then',
        '      if [ -e "$GH_STATE" ]; then echo "gh: could not reach api.github.com" >&2; exit 1; fi',
        '      : > "$GH_STATE"',
        '      exit 0',
        '    fi',
        '    echo v1.0.0',
        '    exit 0 ;;',
        '  "release view")',
        '    if [ "$GH_MODE" = "view-error" ]; then echo "gh: API rate limit exceeded" >&2; exit 1; fi',
        '    if [ "$GH_MODE" = "view-absent" ] || [ "$GH_MODE" = "repo-unreadable" ]; then',
        '      echo "release not found" >&2; exit 1; fi',
        '    echo KawaiiVrc-1.0.0-full.nupkg',
        '    if [ "$GH_MODE" != "missing-feed" ]; then echo releases.win.json; fi',
        '    exit 0 ;;',
        '  "release download")',
        '    if [ "$GH_MODE" = "feed-download-fails" ] || [ "$GH_MODE" = "base-download-fails" ]; then',
        '      echo "download failed: connection reset" >&2; exit 1; fi',
        '    exit 0 ;;',
        '  "repo view")',
        '    if [ "$GH_MODE" = "repo-unreadable" ]; then echo "gh: could not resolve to a repository" >&2; exit 1; fi',
        '    exit 0 ;;',
        'esac',
        'exit 0'
    )
}

# pack-release.ps1 only needs vpk to exist before the fetch. For the publish cases it also
# needs a pack that produces a feed and a matching nupkg, which this writes to VPK_OUT_DIR.
function Write-VpkStub([string]$Directory, [bool]$Windows) {
    if ($Windows) {
        Write-Stub -Path (Join-Path $Directory "vpk.cmd") -NewLine "`r`n" -Lines @(
            '@echo off',
            'if "%VPK_OUT_DIR%"=="" exit /b 0',
            'echo {"Assets":[{"FileName":"KawaiiVrc-%VPK_VERSION%-full.nupkg","Version":"%VPK_VERSION%"}]}>"%VPK_OUT_DIR%\releases.win.json"',
            'type nul > "%VPK_OUT_DIR%\KawaiiVrc-%VPK_VERSION%-full.nupkg"',
            'exit /b 0'
        )
    }
    else {
        Write-Stub -Path (Join-Path $Directory "vpk") -NewLine "`n" -Lines @(
            '#!/bin/sh',
            '[ -n "$VPK_OUT_DIR" ] || exit 0',
            'printf %s "{\"Assets\":[{\"FileName\":\"KawaiiVrc-$VPK_VERSION-full.nupkg\",\"Version\":\"$VPK_VERSION\"}]}" > "$VPK_OUT_DIR/releases.win.json"',
            ': > "$VPK_OUT_DIR/KawaiiVrc-$VPK_VERSION-full.nupkg"',
            'exit 0'
        )
    }
}

# Readiness reads git three ways: the dirty check, the upstream lookup and the unpushed
# count. The publish cases force the version to match the csproj so those checks run.
function Write-GitStub([string]$Directory, [bool]$Windows) {
    if ($Windows) {
        Write-Stub -Path (Join-Path $Directory "git.cmd") -NewLine "`r`n" -Lines @(
            '@echo off',
            'if /I "%1"=="status" goto status_check',
            'if /I "%1"=="rev-parse" goto rev_parse',
            'if /I "%1"=="rev-list" goto rev_list',
            'exit /b 0',
            '',
            ':status_check',
            'if "%GIT_MODE%"=="dirty" goto status_dirty',
            'if "%GIT_MODE%"=="status-error" goto status_error',
            'exit /b 0',
            '',
            ':status_dirty',
            'echo  M VrcCompanion/Program.cs',
            'exit /b 0',
            '',
            ':status_error',
            'echo fatal: not a git repository 1>&2',
            'exit /b 128',
            '',
            ':rev_parse',
            'if "%GIT_MODE%"=="upstream-error" goto upstream_error',
            'if "%GIT_MODE%"=="no-upstream" goto no_upstream',
            'echo origin/master',
            'exit /b 0',
            '',
            ':no_upstream',
            'echo fatal: no upstream configured for branch ''master'' 1>&2',
            'exit /b 128',
            '',
            ':upstream_error',
            'echo fatal: unable to read repository 1>&2',
            'exit /b 128',
            '',
            ':rev_list',
            'if "%GIT_MODE%"=="count-error" goto count_error',
            'echo 0',
            'exit /b 0',
            '',
            ':count_error',
            'echo fatal: bad revision 1>&2',
            'exit /b 128'
        )
        return
    }

    Write-Stub -Path (Join-Path $Directory "git") -NewLine "`n" -Lines @(
        '#!/bin/sh',
        'case "$1" in',
        '  status)',
        '    if [ "$GIT_MODE" = "dirty" ]; then echo " M VrcCompanion/Program.cs"; exit 0; fi',
        '    if [ "$GIT_MODE" = "status-error" ]; then echo "fatal: not a git repository" >&2; exit 128; fi',
        '    exit 0 ;;',
        '  rev-parse)',
        '    if [ "$GIT_MODE" = "no-upstream" ]; then echo "fatal: no upstream configured for branch master" >&2; exit 128; fi',
        '    if [ "$GIT_MODE" = "upstream-error" ]; then echo "fatal: unable to read repository" >&2; exit 128; fi',
        '    echo origin/master; exit 0 ;;',
        '  rev-list)',
        '    if [ "$GIT_MODE" = "count-error" ]; then echo "fatal: bad revision" >&2; exit 128; fi',
        '    echo 0; exit 0 ;;',
        'esac',
        'exit 0'
    )
}

$verifyArguments = @('-VerifyOnly', '-Version', '1.0.0', '-Repo', 'owner/repo')
$publishArguments = @('-Version', $projectVersion, '-Repo', 'owner/repo', '-SkipPublish', '-AllowDirty', '-SkipVerify')
# Same, without -AllowDirty, so the readiness checks run against the stub git.
$readyArguments = @('-Version', $projectVersion, '-Repo', 'owner/repo', '-SkipPublish', '-SkipVerify')
$fetchArguments = @('-Version', '1.0.0', '-Repo', 'owner/repo', '-SkipPublish', '-SkipUpload', '-SkipVerify')

# Markers come from the plain messages, not the rendered error record: PowerShell wraps that
# record with a gutter that ends up inside the text on Linux.
$checks = @(
    @{ Name = "feed asset missing"; Mode = "missing-feed"; Expected = 1; Marker = "has no feed asset"; Arguments = $verifyArguments },
    @{ Name = "listed feed will not download"; Mode = "feed-download-fails"; Expected = 3; Marker = "Could not download the feed"; Arguments = $verifyArguments },
    @{ Name = "chain listing fails"; Mode = "chain-list-fails"; Expected = 3; Marker = "Could not list releases for the chain check"; Arguments = $verifyArguments },
    @{ Name = "delta-base listing fails"; Mode = "fetch-list-fails"; Expected = 1; Marker = "refusing to build a release without its delta base"; Arguments = $fetchArguments },
    @{ Name = "delta-base download fails"; Mode = "base-download-fails"; Expected = 1; Marker = "refusing to build a delta from an incomplete base"; Arguments = $fetchArguments },
    @{ Name = "tag check errors"; Mode = "view-error"; Expected = 1; Marker = "Could not check whether v$projectVersion exists"; Arguments = $publishArguments },
    @{ Name = "absent tag publishes"; Mode = "view-absent"; Expected = 0; Marker = "Published v$projectVersion to"; Arguments = $publishArguments },
    @{ Name = "existing tag is refused"; Mode = "view-exists"; Expected = 1; Marker = "already exists on owner/repo. Bump the version instead of reusing a tag."; Arguments = $publishArguments },
    @{ Name = "upstream check errors"; Mode = "view-absent"; GitMode = "upstream-error"; Expected = 1; Marker = "Could not check the upstream branch"; Arguments = $readyArguments },
    @{ Name = "unpushed count errors"; Mode = "view-absent"; GitMode = "count-error"; Expected = 1; Marker = "Could not count unpushed commits"; Arguments = $readyArguments },
    @{ Name = "no upstream still publishes"; Mode = "view-absent"; GitMode = "no-upstream"; Expected = 0; Marker = "Published v$projectVersion to"; Arguments = $readyArguments },
    @{ Name = "clean tree publishes"; Mode = "view-absent"; GitMode = "clean"; Expected = 0; Marker = "Published v$projectVersion to"; Arguments = $readyArguments },
    @{ Name = "dirty tree is refused"; Mode = "view-absent"; GitMode = "dirty"; Expected = 1; Marker = "Working tree has uncommitted changes"; Arguments = $readyArguments },
    @{ Name = "status check errors"; Mode = "view-absent"; GitMode = "status-error"; Expected = 1; Marker = "git status failed"; Arguments = $readyArguments },
    @{ Name = "unreadable repo refused before the build"; Mode = "repo-unreadable"; Expected = 1; Marker = "Could not read owner/repo"; Arguments = $publishArguments }
)

$savedPath = $env:PATH
$savedMode = $env:GH_MODE
$savedState = $env:GH_STATE
$savedOutDir = $env:VPK_OUT_DIR
$savedVpkVersion = $env:VPK_VERSION
$savedGitMode = $env:GIT_MODE
$failures = 0

try {
    Write-GhStub -Directory $shimDir -Windows $onWindows
    Write-VpkStub -Directory $shimDir -Windows $onWindows
    Write-GitStub -Directory $shimDir -Windows $onWindows
    $env:PATH = "$shimDir$([System.IO.Path]::PathSeparator)$savedPath"
    $env:GH_STATE = Join-Path $shimDir "list-state"
    $env:VPK_OUT_DIR = $releasesDir
    $env:VPK_VERSION = $projectVersion

    # If the stub is not what runs, the checks would silently exercise the real GitHub.
    $stubPath = Join-Path $shimDir $(if ($onWindows) { "gh.cmd" } else { "gh" })
    $resolved = (Get-Command gh -ErrorAction SilentlyContinue).Source
    if ($resolved -ne $stubPath) { throw "gh resolves to '$resolved' instead of the stub at '$stubPath'." }

    $gitStubPath = Join-Path $shimDir $(if ($onWindows) { "git.cmd" } else { "git" })
    $resolvedGit = (Get-Command git -ErrorAction SilentlyContinue).Source
    if ($resolvedGit -ne $gitStubPath) { throw "git resolves to '$resolvedGit' instead of the stub at '$gitStubPath'." }

    $shell = if ($PSVersionTable.PSEdition -eq 'Core') { 'pwsh' } else { 'powershell' }
    $fileArguments = if ($onWindows) {
        @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $releaseScript)
    }
    else {
        @('-NoProfile', '-File', $releaseScript)
    }

    foreach ($check in $checks) {
        $env:GH_MODE = $check.Mode
        $env:GIT_MODE = $check.GitMode
        Remove-Item $env:GH_STATE -ErrorAction SilentlyContinue

        # The failing cases write to stderr, which Windows PowerShell turns into a
        # terminating error when 2>&1 meets ErrorActionPreference = Stop.
        $previous = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            $output = @(& $shell @($fileArguments + $check.Arguments) 2>&1)
            $actual = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previous
        }

        if ($actual -ne $check.Expected) {
            Write-Host ("{0}: exited {1}, expected {2}. Output was:" -f $check.Name, $actual, $check.Expected)
            $output | ForEach-Object { Write-Host "    $_" }
            $failures++
            continue
        }

        # PowerShell wraps long error text and draws a gutter inside the wrapped lines
        # ("refusing to | build"), so drop colour codes, gutters and padding before matching.
        $text = (($output -join "`n") -replace '\x1b\[[0-9;]*m', '') -replace '\s*\|\s*', ' ' -replace '\s+', ' '
        if ($text -notlike "*$($check.Marker)*") {
            Write-Host ("{0}: output did not contain '{1}'. Output was:" -f $check.Name, $check.Marker)
            $output | ForEach-Object { Write-Host "    $_" }
            $failures++
        }
    }

    if (-not (Test-Path $sentinel)) {
        Write-Host "publish output was destroyed: artifacts/publish no longer holds the sentinel."
        $failures++
    }
}
finally {
    $env:PATH = $savedPath
    $env:GH_MODE = $savedMode
    $env:GH_STATE = $savedState
    $env:VPK_OUT_DIR = $savedOutDir
    $env:VPK_VERSION = $savedVpkVersion
    $env:GIT_MODE = $savedGitMode
    Remove-Item $shimDir -Recurse -Force -ErrorAction SilentlyContinue

    # The publish cases pack into artifacts/releases; leave nothing of the stub's behind.
    Remove-Item (Join-Path $releasesDir "releases.win.json") -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $releasesDir "KawaiiVrc-$projectVersion-full.nupkg") -ErrorAction SilentlyContinue

    # Only the sentinel is this run's; the folder holds a real build's output.
    Remove-Item $sentinel -ErrorAction SilentlyContinue
}

$checkCount = $checks.Count + 1

if ($failures -gt 0) {
    Write-Host "$failures of $checkCount feed-parity check(s) failed."
    exit 1
}

Write-Host "$checkCount feed-parity check(s) passed."
