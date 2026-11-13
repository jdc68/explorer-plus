<#
.SYNOPSIS
  Builds the Explorer+ MSI and the winget manifests for it, and optionally installs it.

.DESCRIPTION
  0. Picks the version. Without -Version it takes one patch above both Directory.Build.props and the installed copy,
     and writes it back to Directory.Build.props, so every build is a new, higher version that upgrades the installed one.
  1. Publishes the app (framework-dependent, ReadyToRun) to artifacts\publish\<arch>, with the index service next to it.
  2. Builds installer\ExplorerPlus.Installer.wixproj into artifacts\installer\ExplorerPlus-<version>-<arch>.msi.
  3. Writes winget manifests to artifacts\winget\<version>\, with the MSI's SHA256 filled in. Pass -InstallerUrl with
     the public download URL of the MSI (a GitHub release asset, for example) before submitting them to winget-pkgs.
  4. With -Install: installs the MSI for the current user, then checks that exactly one Explorer+ is installed, that it
     is the version just built, and that its ExplorerPlus.dll is the file just published. -NoStart skips starting
     Explorer+ in the background afterwards (use it from a sandboxed shell such as the Claude desktop app, whose child
     processes see a private copy of HKCU).

.EXAMPLE
  powershell -File installer\build.ps1
  powershell -File installer\build.ps1 -Install
  powershell -File installer\build.ps1 -Version 0.4.0 -InstallerUrl https://example.com/ExplorerPlus-0.4.0-x64.msi
#>
param(
    [string]$Version,
    [ValidateSet("x64", "arm64")] [string]$Arch = "x64",
    [string]$InstallerUrl = "https://REPLACE-ME/ExplorerPlus-{version}-{arch}.msi",
    [switch]$Install,
    [switch]$NoStart
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$upgradeCode = "{1727C5FB-2CAB-40F9-8972-682D1585CA16}" # must match UpgradeCode in Package.wxs

# Explorer+ products installed for this user (normally one), as Code + Version.
function Get-InstalledProducts {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    foreach ($code in $installer.RelatedProducts($upgradeCode)) {
        [pscustomobject]@{ Code = $code; Version = [version]$installer.ProductInfo($code, "VersionString") }
    }
}

$propsPath = Join-Path $root "Directory.Build.props"
if (-not $Version) {
    $propsText = [IO.File]::ReadAllText($propsPath)
    if ($propsText -notmatch '<Version>(\d+\.\d+\.\d+)</Version>') { throw "No <Version>1.2.3</Version> in Directory.Build.props" }
    $current = [version]$Matches[1]
    $installed = Get-InstalledProducts | Sort-Object Version -Descending | Select-Object -First 1
    if ($installed -and $installed.Version -gt $current) { $current = $installed.Version }
    $Version = "{0}.{1}.{2}" -f $current.Major, $current.Minor, ($current.Build + 1)
    [IO.File]::WriteAllText($propsPath, ($propsText -replace '<Version>\d+\.\d+\.\d+</Version>', "<Version>$Version</Version>"))
    Write-Host "Version $Version (written to Directory.Build.props)"
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (MSI versions are numeric): $Version" }

$publish = Join-Path $root "artifacts\publish\$Arch"
$out = Join-Path $root "artifacts\installer"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

Write-Host "Publishing Explorer+ $Version ($Arch)..."
dotnet publish (Join-Path $root "src\ExplorerPlus.App\ExplorerPlus.App.csproj") -c Release -r "win-$Arch" --self-contained false `
    -p:PublishReadyToRun=true -p:Version=$Version -p:DebugType=none -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
# The index service ships inside the app folder (Settings installs it to Program Files from there).
foreach ($file in "ExplorerPlus.IndexService.exe", "ExplorerPlus.IndexService.dll", "ExplorerPlus.IndexService.runtimeconfig.json", "ExplorerPlus.Indexing.dll") {
    if (-not (Test-Path (Join-Path $publish $file))) { throw "$file is missing from the publish folder" }
}

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

if ($Install) {
    $logs = Join-Path $root "logs"
    New-Item -ItemType Directory -Force $logs | Out-Null
    $log = Join-Path $logs "install-$Version.log"
    $msiArgs = @("/i", "`"$msi`"", "/qn", "/l*v", "`"$log`"")
    if ($NoStart) { $msiArgs += "NOSTART=1" }
    Write-Host ""
    Write-Host "Installing $Version..."
    $p = Start-Process msiexec.exe -ArgumentList $msiArgs -Wait -PassThru
    if ($p.ExitCode -notin 0, 3010) { throw "msiexec failed with exit code $($p.ExitCode), see $log" }

    # The installed copy must be exactly what was just built, with no older copy left next to it.
    $products = @(Get-InstalledProducts)
    if ($products.Count -ne 1) { throw "Expected one installed Explorer+, found $($products.Count): $($products.Version -join ', ')" }
    if ($products[0].Version -ne [version]$Version) { throw "Installed version is $($products[0].Version), expected $Version" }
    $dll = Join-Path $env:LOCALAPPDATA "Programs\Explorer+\ExplorerPlus.dll"
    $fileVersion = (Get-Item $dll).VersionInfo.FileVersion
    if ($fileVersion -ne "$Version.0") { throw "$dll is $fileVersion, expected $Version.0" }
    if ((Get-FileHash $dll).Hash -ne (Get-FileHash (Join-Path $publish "ExplorerPlus.dll")).Hash) { throw "$dll differs from the build in $publish" }
    $note = if ($p.ExitCode -eq 3010) { " (a restart finishes replacing files that were in use)" } else { "" }
    Write-Host "Installed: Explorer+ $Version, files match the build$note"
}
