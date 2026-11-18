# Semver comparisons for the release checks. [version] throws on prerelease strings
# such as 1.0.0-beta, so compare the numeric core first and treat a prerelease as lower
# than the matching release. Build metadata is ignored, as semver requires.

function Split-Semver([string]$Text) {
    if ($Text -notmatch '^v?(\d+(?:\.\d+)*)(?:-([0-9A-Za-z.\-]+))?(?:\+[0-9A-Za-z.\-]+)?$') {
        throw "Semver expected but got '$Text'."
    }

    $numbers = @($Matches[1].Split('.') | ForEach-Object { [int]$_ })
    return [pscustomobject]@{
        Numbers = $numbers
        Prerelease = "$($Matches[2])"
    }
}

function Compare-Semver([string]$Left, [string]$Right) {
    $a = Split-Semver $Left
    $b = Split-Semver $Right

    $width = [Math]::Max($a.Numbers.Count, $b.Numbers.Count)
    for ($i = 0; $i -lt $width; $i++) {
        $av = if ($i -lt $a.Numbers.Count) { $a.Numbers[$i] } else { 0 }
        $bv = if ($i -lt $b.Numbers.Count) { $b.Numbers[$i] } else { 0 }
        if ($av -ne $bv) { return [Math]::Sign($av - $bv) }
    }

    if ($a.Prerelease -eq $b.Prerelease) { return 0 }
    if (-not $a.Prerelease) { return 1 }
    if (-not $b.Prerelease) { return -1 }
    return [Math]::Sign([string]::CompareOrdinal($a.Prerelease, $b.Prerelease))
}

# Descending order. Release lists are short, so an insertion sort keeps the ordering
# rules in one place instead of duplicating them in a Sort-Object key.
function Sort-Semver([string[]]$Values) {
    $sorted = @($Values)
    for ($i = 1; $i -lt $sorted.Count; $i++) {
        $current = $sorted[$i]
        $j = $i - 1
        while ($j -ge 0 -and (Compare-Semver $sorted[$j] $current) -lt 0) {
            $sorted[$j + 1] = $sorted[$j]
            $j--
        }

        $sorted[$j + 1] = $current
    }

    return $sorted
}
