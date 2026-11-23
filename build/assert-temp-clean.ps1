# Fails when a suite run left its scratch in %TEMP%: the per-run folder the module initializer
# makes, the loose files a test names after its GUID, or anything in the cache roots. The
# retained entries - the log folder the guard reads, the WebView2 profile and the shared
# settings folder - are infrastructure and are skipped.
# The naming rules mirror TestProfile.ClearScratch; if one side changes, this check fails and
# says which entry the sweep missed.
$ErrorActionPreference = "Stop"

$temp = [System.IO.Path]::GetTempPath()
$guid = '[0-9a-fA-F]{32}|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}'
$prefixes = @("vrccompanion-", "vrc-")
$runFolder = "vrc-companion-tests-"
$kept = @("logs", "shared", "browser")
$problems = New-Object System.Collections.Generic.List[string]

function Test-TestPrefix([string] $name) {
    foreach ($prefix in $prefixes) {
        if ($name.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

# The per-run folder goes when its process exits, so after the run none may remain.
Get-ChildItem -LiteralPath $temp -Directory -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name.StartsWith($runFolder, [System.StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object { $problems.Add("per-run folder " + $_.Name) }

# Loose entries a test dropped straight into %TEMP%, named with a prefix and its GUID.
Get-ChildItem -LiteralPath $temp -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notlike "$runFolder*" } |
    Where-Object { (Test-TestPrefix $_.Name) -and ($_.Name -match $guid) } |
    ForEach-Object { $problems.Add("temp entry " + $_.Name) }

# The cache roots stay as folders but hold nothing once the sweep has run.
$tests = Join-Path $temp "VrcCompanionTests"
$roots = @(
    $tests,
    (Join-Path $tests "shared"),
    (Join-Path $temp "VrcMediaMeta"),
    (Join-Path $temp "VrcCompanion.Tests"),
    (Join-Path $temp "VrcMediaTests")
)
foreach ($root in $roots) {
    if (-not (Test-Path -LiteralPath $root)) {
        continue
    }

    Get-ChildItem -LiteralPath $root -Force -ErrorAction SilentlyContinue |
        Where-Object { $kept -notcontains $_.Name } |
        ForEach-Object { $problems.Add("under " + (Split-Path -Leaf $root) + ": " + $_.Name) }
}

if ($problems.Count -gt 0) {
    Write-Host "The suite left scratch behind in $temp :"
    $problems | Sort-Object -Unique | ForEach-Object { Write-Host ("  " + $_) }
    Write-Host "Set VRCC_KEEP_TEST_SCRATCH=1 deliberately to keep a run's files; a normal run must sweep them."
    exit 1
}

Write-Host "No test scratch left in $temp."
