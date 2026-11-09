<#
.SYNOPSIS
  Builds the Explorer+ MSI and the winget manifests for it.

.DESCRIPTION
  1. Publishes the app (framework-dependent, ReadyToRun) to artifacts\publish\<arch>.
  2. Builds installer\ExplorerPlus.Installer.wixproj into artifacts\installer\ExplorerPlus-<version>-<arch>.msi.
  3. Writes winget manifests to artifacts\winget\<version>\, with the MSI's SHA256 filled in. Pass -InstallerUrl with
     the public download URL of the MSI (a GitHub release asset, for example) before submitting them to winget-pkgs.

.EXAMPLE
  powershell -File installer\build.ps1
  powershell -File installer\build.ps1 -Version 0.4.0 -InstallerUrl https://example.com/ExplorerPlus-0.4.0-x64.msi
#>
param(
    [string]$Version,
    [ValidateSet("x64", "arm64")] [string]$Arch = "x64",
    [string]$InstallerUrl = "https://REPLACE-ME/ExplorerPlus-{version}-{arch}.msi"
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

if (-not $Version) {
    [xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
    $Version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (MSI versions are numeric): $Version" }

$publish = Join-Path $root "artifacts\publish\$Arch"
$out = Join-Path $root "artifacts\installer"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

Write-Host "Publishing Explorer+ $Version ($Arch)..."
dotnet publish (Join-Path $root "src\ExplorerPlus.App\ExplorerPlus.App.csproj") -c Release -r "win-$Arch" --self-contained false `
    -p:PublishReadyToRun=true -p:Version=$Version -p:DebugType=none -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "Building the MSI..."
dotnet build (Join-Path $PSScriptRoot "ExplorerPlus.Installer.wixproj") -c Release -p:Platform=$Arch `
    -p:ProductVersion=$Version -p:PublishDir=$publish -o $out
if ($LASTEXITCODE -ne 0) { throw "MSI build failed" }

$msi = Join-Path $out "ExplorerPlus-$Version-$Arch.msi"
$sha = (Get-FileHash $msi -Algorithm SHA256).Hash
$url = $InstallerUrl.Replace("{version}", $Version).Replace("{arch}", $Arch)

$manifests = Join-Path $root "artifacts\winget\$Version"
New-Item -ItemType Directory -Force $manifests | Out-Null
foreach ($template in Get-ChildItem (Join-Path $PSScriptRoot "winget") -Filter *.yaml) {
    $text = (Get-Content $template.FullName -Raw).Replace("{version}", $Version).Replace("{arch}", $Arch).Replace("{url}", $url).Replace("{sha256}", $sha)
    # winget-pkgs wants UTF-8 without a BOM.
    [IO.File]::WriteAllText((Join-Path $manifests $template.Name), $text, (New-Object Text.UTF8Encoding $false))
}

Write-Host ""
Write-Host "MSI:       $msi"
Write-Host "SHA256:    $sha"
Write-Host "Manifests: $manifests"
if ($url -like "*REPLACE-ME*") { Write-Host "Before submitting to winget, run again with -InstallerUrl set to where the MSI is published." }
