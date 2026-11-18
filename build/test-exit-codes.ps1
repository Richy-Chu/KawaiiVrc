# Checks the verifier's exit-code classification without contacting GitHub. A 403 (rate
# limit) must map to the infrastructure code, or a throttled check would open a drift issue.
$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "ReleaseVerifier\ReleaseVerifier.csproj"
dotnet build $project -c Release --nologo -v quiet | Out-Null
if ($LASTEXITCODE -ne 0) { throw "ReleaseVerifier did not build (exit $LASTEXITCODE)." }

$dll = @(Get-ChildItem (Join-Path $PSScriptRoot "ReleaseVerifier\bin\Release\*\ReleaseVerifier.dll") -File | Select-Object -First 1)
if (-not $dll) { throw "ReleaseVerifier.dll was not produced by the build." }

$cases = @(
    @{ Status = 401; Expected = 3 },
    @{ Status = 403; Expected = 3 },
    @{ Status = 408; Expected = 3 },
    @{ Status = 429; Expected = 3 },
    @{ Status = 500; Expected = 3 },
    @{ Status = 404; Expected = 1 },
    @{ Status = 400; Expected = 1 }
)

$failures = 0
foreach ($case in $cases) {
    dotnet $dll.FullName --classify-status $case.Status | Out-Null
    $actual = $LASTEXITCODE
    if ($actual -ne $case.Expected) {
        Write-Host ("status {0}: mapped to exit {1}, expected {2}" -f $case.Status, $actual, $case.Expected)
        $failures++
    }
}

if ($failures -gt 0) {
    Write-Host "$failures of $($cases.Count) classification case(s) failed."
    exit 1
}

Write-Host "$($cases.Count) exit-code classification case(s) passed."
