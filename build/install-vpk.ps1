# Installs the vpk tool at the version the projects reference. The patched nupkg is rebuilt
# by the Update.exe shipped inside vpk, which build/ReleaseVerifier looks up under
# ~/.dotnet/tools/.store, so vpk has to match the Velopack package the app ships.
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$projects = @(
    @{ Path = (Join-Path $root "VrcCompanion\VrcCompanion.csproj"); Name = "VrcCompanion/VrcCompanion.csproj" },
    @{ Path = (Join-Path $root "build\ReleaseVerifier\ReleaseVerifier.csproj"); Name = "build/ReleaseVerifier/ReleaseVerifier.csproj" }
)

$versions = @($projects | ForEach-Object {
    if (-not (Test-Path $_.Path)) { throw "Missing project file: $($_.Name)" }

    # Read the project as XML so attribute, child-element and multi-line forms all work.
    $doc = [xml](Get-Content -Raw $_.Path)
    $reference = $null
    foreach ($group in @($doc.Project.ItemGroup)) {
        foreach ($item in @($group.PackageReference)) {
            if ($item -and $item.Include -eq "Velopack") { $reference = $item; break }
        }
        if ($reference) { break }
    }

    if (-not $reference) { throw "No Velopack PackageReference found in $($_.Name)." }
    if (-not $reference.Version) { throw "Velopack in $($_.Name) has no Version." }
    "$($reference.Version)".Trim()
})

# A patcher older than the Velopack package would test a different delta path than users
# run, so a half-finished bump fails here instead of verifying anyway.
$distinct = @($versions | Sort-Object -Unique)
if ($distinct.Count -ne 1) {
    $names = ($projects.Name) -join " and "
    throw "Velopack versions disagree: $names reference $($versions -join ' and ')."
}

$version = $distinct[0]
Write-Host "Installing vpk $version to match the referenced Velopack package."
dotnet tool install --global vpk --version $version
if ($LASTEXITCODE -ne 0) { throw "vpk $version could not be installed." }
