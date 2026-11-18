# Lints the build scripts with PSScriptAnalyzer and the settings beside this file. Exits 1
# when anything is reported, so CI fails instead of printing warnings nobody reads.
$ErrorActionPreference = "Stop"

Import-Module PSScriptAnalyzer -ErrorAction Stop

$settings = Join-Path $PSScriptRoot "PSScriptAnalyzerSettings.psd1"
$scripts = @(Get-ChildItem -Path (Join-Path $PSScriptRoot "*.ps1") -File)
if ($scripts.Count -eq 0) { throw "No PowerShell scripts found in $PSScriptRoot." }

# Invoke-ScriptAnalyzer takes one path per call.
$findings = @(foreach ($script in $scripts) {
    Invoke-ScriptAnalyzer -Path $script.FullName -Settings $settings -Severity Warning, Error
})
if ($findings.Count -eq 0) {
    Write-Host "PSScriptAnalyzer: $($scripts.Count) script(s) clean."
    exit 0
}

$findings | Sort-Object ScriptName, Line | ForEach-Object {
    Write-Host ("{0}:{1}  {2}  [{3}]" -f (Split-Path $_.ScriptName -Leaf), $_.Line, $_.Message, $_.RuleName)
}
Write-Host "PSScriptAnalyzer reported $($findings.Count) issue(s) across $($scripts.Count) script(s)."
exit 1
