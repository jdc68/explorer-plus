<#
Measures the performance budget (PLAN.md section 8) on this machine. Run by hand; build.ps1 doesn't run it.

Publishes a ReadyToRun build like the installer's, makes scratch folders of 100,000 and 10,000 files, and starts the
build in its own profile (EXPLORERPLUS_PROFILE=perf) with EXPLORERPLUS_PERF set, so Services/PerfProbe in the app
measures itself and writes JSON. Writes a table to artifacts/perf/.
#>
param(
    [ValidateSet("x64", "arm64")] [string]$Arch = "x64",
    [int]$StartRuns = 5,
    [int]$BackgroundRuns = 3,
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$perf = Join-Path $root "artifacts\perf"
$build = Join-Path $perf "build"
$data = Join-Path $perf "data"
$exe = Join-Path $build "ExplorerPlus.exe"

if (-not $SkipPublish -or -not (Test-Path $exe)) {
    Write-Host "Publishing a ReadyToRun build..."
    $project = Join-Path $root "src\ExplorerPlus.App\ExplorerPlus.App.csproj"
    $output = dotnet restore $project -r "win-$Arch" -p:PublishReadyToRun=true
    if ($LASTEXITCODE -ne 0) { $output | Write-Host; throw "dotnet restore failed" }
    $output = dotnet publish $project -c Release -r "win-$Arch" --self-contained false -p:PublishReadyToRun=true -p:DebugType=none -o $build
    if ($LASTEXITCODE -ne 0) { $output | Write-Host; throw "dotnet publish failed" }
}

function New-Files([string]$dir, [int]$count) {
    if ((Test-Path $dir) -and @(Get-ChildItem $dir -File).Count -eq $count) { return }
    Write-Host "Creating $count files in $dir..."
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory $dir | Out-Null
    $exts = "txt", "log", "csv", "json", "md", "xml"
    for ($i = 0; $i -lt $count; $i++) {
        $chunk = New-Object byte[] ($i % 512)
        [IO.File]::WriteAllBytes((Join-Path $dir ("file{0:D6}.{1}" -f $i, $exts[$i % $exts.Count])), $chunk)
    }
}

New-Files (Join-Path $data "100k") 100000
New-Files (Join-Path $data "10k") 10000

$env:EXPLORERPLUS_PROFILE = "perf"
$env:EXPLORERPLUS_PERF = $data
$profileDir = Join-Path $env:APPDATA "ExplorerPlus\profiles\perf"
if (Test-Path $profileDir) { Remove-Item $profileDir -Recurse -Force }

function Invoke-Run([string]$mode, [string[]]$arguments, [int]$timeoutSeconds) {
    $env:EXPLORERPLUS_PERF_MODE = $mode
    $result = Join-Path $data "result-$mode.json"
    if (Test-Path $result) { Remove-Item $result }
    $p = if ($arguments) { Start-Process $exe -ArgumentList $arguments -PassThru } else { Start-Process $exe -PassThru }
    if (-not $p.WaitForExit($timeoutSeconds * 1000)) { $p.Kill(); throw "$mode run did not finish in $timeoutSeconds s" }
    if (-not (Test-Path $result)) { throw "$mode run wrote no result" }
    $json = Get-Content $result -Raw | ConvertFrom-Json
    if ($json.error) { throw "$mode run failed: $($json.error)" }
    $json
}

function Get-Median([double[]]$values) {
    $sorted = $values | Sort-Object
    $sorted[[int][Math]::Floor($sorted.Count / 2)]
}

function Format-Ms([double]$ms) { "{0:N0} ms" -f $ms }

Write-Host "Warm-up start (first start of a fresh profile, not counted)..."
Invoke-Run "start" @() 60 | Out-Null

$starts = @()
for ($i = 1; $i -le $StartRuns; $i++) {
    Write-Host "Start $i of $StartRuns..."
    $starts += Invoke-Run "start" @() 60
    Start-Sleep -Seconds 2
}

$backgrounds = @()
for ($i = 1; $i -le $BackgroundRuns; $i++) {
    Write-Host "Background start $i of $BackgroundRuns..."
    $env:EXPLORERPLUS_PERF_MODE = "background"
    $result = Join-Path $data "result-background.json"
    if (Test-Path $result) { Remove-Item $result }
    $primary = Start-Process $exe -ArgumentList "--background" -PassThru
    Start-Sleep -Seconds 6 # let it prewarm its window
    $t0 = [DateTime]::UtcNow
    Start-Process $exe | Out-Null
    if (-not $primary.WaitForExit(60000)) { $primary.Kill(); throw "background run did not finish" }
    $json = Get-Content $result -Raw | ConvertFrom-Json
    if ($json.error) { throw "background run failed: $($json.error)" }
    $backgrounds += ([DateTime]::Parse($json.firstRowsUtc).ToUniversalTime() - $t0).TotalMilliseconds
    Start-Sleep -Seconds 2
}

Write-Host "Full run (about 2 minutes, two of them idle; leave the window alone)..."
$full = Invoke-Run "full" @() 600

$open100k = @($full.open100k)
$open10k = @($full.open10k)
$filter = @($full.filter10k | ForEach-Object { $_.ms })
$filterBusy = @($full.filter10k | ForEach-Object { $_.busyMs })
$menus = @($full.itemMenuMs | Where-Object { $_ -ne $null })
$cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name.Trim()
$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

$rows = @(
    @("Cold start to usable window (Home)", "< 400 ms",
        "first rows $(Format-Ms (Get-Median @($starts | ForEach-Object { $_.startToFirstRowsMs }))) median of $StartRuns (window $(Format-Ms (Get-Median @($starts | ForEach-Object { $_.startToWindowMs }))), file cache warm)"),
    @("Window from background mode", "-", "first rows $(Format-Ms (Get-Median $backgrounds)) median of $BackgroundRuns, from the second launch"),
    @("Open local folder, first rows (10,000 files)", "< 50 ms",
        "$(Format-Ms (Get-Median @($open10k | ForEach-Object { $_.firstRowsMs }))) median, full list $(Format-Ms (Get-Median @($open10k | ForEach-Object { $_.fullListMs })))"),
    @("100,000-file folder fully listed", "< 1.5 s, UI responsive throughout",
        "$(Format-Ms (Get-Median @($open100k | ForEach-Object { $_.fullListMs }))) median (first rows $(Format-Ms (Get-Median @($open100k | ForEach-Object { $_.firstRowsMs })))); longest UI stall $(Format-Ms (($open100k | ForEach-Object { $_.maxUiLagMs } | Measure-Object -Maximum).Maximum))"),
    @("Right-click menu (own)", "< 30 ms",
        "first $(Format-Ms $menus[0]), then $(Format-Ms (Get-Median @($menus | Select-Object -Skip 1))) median"),
    @("Type-to-filter, 10k items", "< 16 ms per keystroke",
        "$(Format-Ms (Get-Median $filter)) median to the frame, UI busy $(Format-Ms (Get-Median $filterBusy)) median ($(Format-Ms (($filterBusy | Measure-Object -Maximum).Maximum)) worst of $($filter.Count))"),
    @("Scrolling, 10k items (Details)", "-",
        "page step $(Format-Ms (Get-Median @($full.scroll10k.pages))), tenth-of-a-page step $(Format-Ms (Get-Median @($full.scroll10k.wheel))) median, each until the UI is idle"),
    @("Idle memory, 1 tab (Home)", "-",
        ("{0:N0} MB working set, {1:N0} MB private" -f $full.memory1Tab.workingSetMb, $full.memory1Tab.privateMb)),
    @("Idle memory, 3 tabs", "< 150 MB",
        ("{0:N0} MB working set, {1:N0} MB private (Home, C:\Windows, System32)" -f $full.memory3Tabs.workingSetMb, $full.memory3Tabs.privateMb))
)

$stamp = Get-Date -Format "yyyy-MM-dd HHmm"
$lines = @("# Performance, $stamp", "", "Explorer+ $version, $cpu, $($full.cpus) logical processors, $Arch ReadyToRun build.", "",
    "| Scenario | Target | Measured |", "|---|---|---|")
$lines += $rows | ForEach-Object { "| $($_[0]) | $($_[1]) | $($_[2]) |" }
$report = Join-Path $perf "perf-$($stamp.Replace(' ', '-')).md"
$lines | Set-Content $report -Encoding utf8
Copy-Item (Join-Path $data "result-full.json") ($report -replace "\.md$", "-full.json")
$lines | ForEach-Object { Write-Host $_ }
Write-Host ""
Write-Host "Written to $report"
