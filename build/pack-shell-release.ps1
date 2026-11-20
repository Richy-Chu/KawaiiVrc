param(
  [Parameter(Mandatory = $true)][string]$Version,
  [string]$KeyPath = (Join-Path $env:LOCALAPPDATA 'KawaiiVrcShell\keys\shell.key'),
  [string]$Repo = 'Richy-Chu/KawaiiVrc',
  [string]$OutDir = '',
  [string]$Tag = 'shell-latest'
)

# Builds the signed NSIS installer plus the latest.json the shell's updater reads.
# The signing key lives outside the repository; never commit it or its password.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$shell = Join-Path $root 'shell'
if (-not $OutDir) { $OutDir = Join-Path $root 'artifacts\shell' }

if (-not (Test-Path $KeyPath)) { throw "Signing key not found at $KeyPath" }

# The key is password protected; the password comes from the environment so it
# never lands in the repository or in this script.
$password = $env:KAWAII_SHELL_KEY_PASSWORD
if ([string]::IsNullOrWhiteSpace($password)) {
  throw "Set KAWAII_SHELL_KEY_PASSWORD to the signing key password before running this script."
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$config = Get-Content (Join-Path $shell 'tauri.conf.json') -Raw | ConvertFrom-Json
if ($config.version -ne $Version) {
  throw "tauri.conf.json says $($config.version) but this release is $Version"
}

$env:TAURI_SIGNING_PRIVATE_KEY = (Get-Content $KeyPath -Raw).Trim()
$env:TAURI_SIGNING_PRIVATE_KEY_PASSWORD = $password

Push-Location $shell
try {
  & npx --yes '@tauri-apps/cli@2' build --bundles nsis
  if ($LASTEXITCODE -ne 0) { throw "tauri build failed with exit code $LASTEXITCODE" }
} finally {
  Pop-Location
}

$bundleDir = Join-Path $shell 'target\release\bundle\nsis'
$installer = Get-ChildItem $bundleDir -Filter '*_x64-setup.exe' |
  Where-Object { $_.Name -like "*_$($Version)_*" } |
  Select-Object -First 1
if (-not $installer) { throw "No installer for $Version in $bundleDir" }

$signaturePath = "$($installer.FullName).sig"
if (-not (Test-Path $signaturePath)) { throw "tauri did not write $signaturePath" }

$assetName = "KawaiiVrc-Shell_${Version}_x64-setup.exe"
Copy-Item $installer.FullName (Join-Path $OutDir $assetName) -Force
$signature = (Get-Content $signaturePath -Raw).Trim()
Set-Content -Path (Join-Path $OutDir "$assetName.sig") -Value $signature -Encoding utf8

$manifest = [ordered]@{
  version   = $Version
  notes     = "KawaiiVrc Rust shell $Version"
  pub_date  = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
  platforms = [ordered]@{
    'windows-x86_64' = [ordered]@{
      signature = $signature
      url       = "https://github.com/$Repo/releases/download/$Tag/$assetName"
    }
  }
}
$manifestPath = Join-Path $OutDir 'shell-latest.json'
# Written without a BOM: the updater parses this as plain JSON.
$json = $manifest | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText($manifestPath, $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "installer: $assetName"
Write-Host "manifest : $manifestPath"
Write-Host "replace the assets on the '$Tag' GitHub release with shell-latest.json and $assetName"
