# Checks the verifier's exit-code classification without contacting GitHub. A 403 (rate
# limit) must map to the infrastructure code, or a throttled check would open a drift issue,
# and a missing patcher must do the same without touching the real Update.exe files.
$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "ReleaseVerifier\ReleaseVerifier.csproj"
dotnet build $project -c Release --nologo -v quiet | Out-Null
if ($LASTEXITCODE -ne 0) { throw "ReleaseVerifier did not build (exit $LASTEXITCODE)." }

$dll = @(Get-ChildItem (Join-Path $PSScriptRoot "ReleaseVerifier\bin\Release\*\ReleaseVerifier.dll") -File | Select-Object -First 1)
if (-not $dll) { throw "ReleaseVerifier.dll was not produced by the build." }

$checks = @(
    @{ Name = "status 401"; Arguments = @("--classify-status", "401"); Expected = 3 },
    @{ Name = "status 403"; Arguments = @("--classify-status", "403"); Expected = 3 },
    @{ Name = "status 408"; Arguments = @("--classify-status", "408"); Expected = 3 },
    @{ Name = "status 429"; Arguments = @("--classify-status", "429"); Expected = 3 },
    @{ Name = "status 500"; Arguments = @("--classify-status", "500"); Expected = 3 },
    @{ Name = "status 404"; Arguments = @("--classify-status", "404"); Expected = 1 },
    @{ Name = "status 400"; Arguments = @("--classify-status", "400"); Expected = 1 },
    @{ Name = "missing Update.exe"; Arguments = @("--classify-missing-update-exe"); Expected = 3 }
)

$failures = 0
foreach ($check in $checks) {
    $arguments = @($check.Arguments)
    dotnet $dll.FullName @arguments | Out-Null
    $actual = $LASTEXITCODE
    if ($actual -ne $check.Expected) {
        Write-Host ("{0}: exited {1}, expected {2}" -f $check.Name, $actual, $check.Expected)
        $failures++
    }
}

if ($failures -gt 0) {
    Write-Host "$failures of $($checks.Count) classification check(s) failed."
    exit 1
}

Write-Host "$($checks.Count) exit-code classification check(s) passed."
