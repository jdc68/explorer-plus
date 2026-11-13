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
  5. With -Release: publishes the build as a public GitHub release. Before anything changes it checks that the working
     tree is clean and gh is signed in, shows the version and the notes, and asks (-Yes answers for you). After the
     build it commits the version bump, pushes the branch, tags the commit v<version>, pushes the tag and runs
     gh release create with the MSI attached. The winget manifests get the release asset's URL. Notes are the commit
     subjects since the last v* tag, after -Notes if given.

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
    [switch]$NoStart,
    [switch]$Release,
    [string]$Notes,
    [switch]$Yes
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

# stderr is left on the console: redirected, Windows PowerShell turns it into errors that stop the script
function Invoke-Git {
    $output = & git -C $root @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed (exit $LASTEXITCODE)" }
    $output
}

if ($Release) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "-Release needs the GitHub CLI (gh)" }
    & gh auth status > $null
    if ($LASTEXITCODE -ne 0) { throw "gh is not signed in (run gh auth login)" }
    if (Invoke-Git status --porcelain) { throw "-Release needs a clean working tree, so the release matches a commit. Commit or stash first." }
    $branch = Invoke-Git rev-parse --abbrev-ref HEAD
    if ($branch -eq "HEAD") { throw "-Release needs a branch, not a detached HEAD" }
    $repo = & gh repo view --json nameWithOwner -q .nameWithOwner
    if ($LASTEXITCODE -ne 0 -or -not $repo) { throw "gh can't tell which GitHub repository this is" }
}

$propsPath = Join-Path $root "Directory.Build.props"
$bumped = $false
if (-not $Version) {
    $propsText = [IO.File]::ReadAllText($propsPath)
    if ($propsText -notmatch '<Version>(\d+\.\d+\.\d+)</Version>') { throw "No <Version>1.2.3</Version> in Directory.Build.props" }
    $current = [version]$Matches[1]
    $installed = Get-InstalledProducts | Sort-Object Version -Descending | Select-Object -First 1
    if ($installed -and $installed.Version -gt $current) { $current = $installed.Version }
    $Version = "{0}.{1}.{2}" -f $current.Major, $current.Minor, ($current.Build + 1)
    $bumped = $true
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (MSI versions are numeric): $Version" }

if ($Release) {
    $tag = "v$Version"
    if (Invoke-Git tag --list $tag) { throw "Tag $tag already exists" }
    # Commit subjects since the last release, newest first
    $lastTag = Invoke-Git tag --list "v*" --merged HEAD --sort=-v:refname | Select-Object -First 1
    $range = if ($lastTag) { "$lastTag..HEAD" } else { "HEAD" }
    $changes = @(Invoke-Git log $range --no-merges --pretty=format:"- %s") | Where-Object { $_ -and $_ -notmatch '^- Release \d' }
    $body = @()
    if ($Notes) { $body += $Notes, "" }
    if ($changes) { $body += "Changes:", "" ; $body += $changes; $body += "" }
    $body += "Install: download ExplorerPlus-$Version-$Arch.msi below and run it. It installs for the current user, without admin rights, and needs the .NET 9 Desktop Runtime."
    $releaseNotes = $body -join "`n"
    if ($InstallerUrl -like "*REPLACE-ME*") { $InstallerUrl = "https://github.com/$repo/releases/download/$tag/ExplorerPlus-{version}-{arch}.msi" }

    Write-Host "Release $tag of $repo from $branch, notes:"
    Write-Host ""
    Write-Host $releaseNotes
    Write-Host ""
    if (-not $Yes) {
        $answer = $null
        try { $answer = Read-Host "Publish $tag as a public GitHub release? (y/N)" } catch { }
        if ($answer -notmatch '^(y|yes)$') { Write-Host "Not released; nothing was changed."; exit 1 }
    }
}

# a release commits the version it was built with, even one given with -Version
if ($bumped -or $Release) {
    $propsText = [IO.File]::ReadAllText($propsPath)
    [IO.File]::WriteAllText($propsPath, ($propsText -replace '<Version>\d+\.\d+\.\d+</Version>', "<Version>$Version</Version>"))
    Write-Host "Version $Version (written to Directory.Build.props)"
}

$publish = Join-Path $root "artifacts\publish\$Arch"
$out = Join-Path $root "artifacts\installer"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

Write-Host "Publishing Explorer+ $Version ($Arch)..."
# after a plain build the WPF temp project reuses a restore without this runtime (NETSDK1047)
dotnet restore (Join-Path $root "src\ExplorerPlus.App\ExplorerPlus.App.csproj") -r "win-$Arch" -p:PublishReadyToRun=true
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }
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

if ($Release) {
    Write-Host ""
    Write-Host "Publishing release $tag..."
    if (Invoke-Git status --porcelain) {
        Invoke-Git add -- Directory.Build.props | Out-Null
        Invoke-Git commit -m "Release $Version" | Out-Null
    }
    Invoke-Git push origin $branch | Out-Null
    Invoke-Git tag -a $tag -m "Explorer+ $Version" | Out-Null
    Invoke-Git push origin $tag | Out-Null
    $notesFile = Join-Path ([IO.Path]::GetTempPath()) "explorerplus-release-$Version.md"
    [IO.File]::WriteAllText($notesFile, $releaseNotes, (New-Object Text.UTF8Encoding $false))
    & gh release create $tag $msi --repo $repo --title "Explorer+ $Version" --notes-file $notesFile --verify-tag
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed; tag $tag is pushed, retry with: gh release create $tag `"$msi`" --notes-file `"$notesFile`"" }
    Remove-Item $notesFile -ErrorAction SilentlyContinue
    Write-Host "Released:  https://github.com/$repo/releases/tag/$tag"
}

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
