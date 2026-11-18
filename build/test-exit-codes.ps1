# Checks the verifier's exit-code classification without contacting GitHub. A 403 (rate
# limit) must map to the infrastructure code, or a throttled check would open a drift issue,
# and a missing patcher must report the same, with the marker the caller relies on.
$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "ReleaseVerifier\ReleaseVerifier.csproj"
dotnet build $project -c Release --nologo -v quiet | Out-Null
if ($LASTEXITCODE -ne 0) { throw "ReleaseVerifier did not build (exit $LASTEXITCODE)." }

$dll = @(Get-ChildItem (Join-Path $PSScriptRoot "ReleaseVerifier\bin\Release\*\ReleaseVerifier.dll") -File | Select-Object -First 1)
if (-not $dll) { throw "ReleaseVerifier.dll was not produced by the build." }

$missingPatcher = Join-Path ([System.IO.Path]::GetTempPath()) "kawaiivrc-no-such-Update.exe"

$checks = @(
    @{ Name = "status 401"; Arguments = @("--classify-status", "401"); Expected = 3 },
    @{ Name = "status 403"; Arguments = @("--classify-status", "403"); Expected = 3 },
    @{ Name = "status 408"; Arguments = @("--classify-status", "408"); Expected = 3 },
    @{ Name = "status 429"; Arguments = @("--classify-status", "429"); Expected = 3 },
    @{ Name = "status 500"; Arguments = @("--classify-status", "500"); Expected = 3 },
    @{ Name = "status 404"; Arguments = @("--classify-status", "404"); Expected = 1 },
    @{ Name = "status 400"; Arguments = @("--classify-status", "400"); Expected = 1 },
    @{
        Name = "missing Update.exe"
        Arguments = @("--repo", "owner/repo", "--from", "1.0.0", "--target", "1.0.1", "--update-exe", $missingPatcher)
        Expected = 3
        Marker = "INFRASTRUCTURE:"
    }
)

$failures = 0
foreach ($check in $checks) {
    $arguments = @($check.Arguments)
    $output = @(dotnet $dll.FullName @arguments 2>&1)
    $actual = $LASTEXITCODE
    if ($actual -ne $check.Expected) {
        Write-Host ("{0}: exited {1}, expected {2}" -f $check.Name, $actual, $check.Expected)
        $failures++
        continue
    }

    if ($check.Marker -and ("$($output -join "`n")" -notlike "*$($check.Marker)*")) {
        Write-Host ("{0}: output did not contain '{1}'" -f $check.Name, $check.Marker)
        $failures++
    }
}

if ($failures -gt 0) {
    Write-Host "$failures of $($checks.Count) classification check(s) failed."
    exit 1
}

Write-Host "$($checks.Count) exit-code classification check(s) passed."
