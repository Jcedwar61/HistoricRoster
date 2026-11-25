# Builds dist\HistoricRosterSetup.exe: publishes the program into dist\HistoricRoster, then runs NSIS.
# Needs the .NET 8 SDK and NSIS 3 (makensis.exe).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$dotnet = @("$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe", 'dotnet') | Where-Object { $_ -eq 'dotnet' -or (Test-Path $_) } | Select-Object -First 1
$makensis = @("${env:ProgramFiles(x86)}\NSIS\makensis.exe", "$env:ProgramFiles\NSIS\makensis.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $makensis) { throw 'NSIS not found: install NSIS 3 (https://nsis.sourceforge.io).' }

[xml]$proj = Get-Content "$root\App\HistoricRosterApp.csproj"
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

# A clean staging folder every time: incremental publishes into a reused folder can leave files out.
$out = "$root\build\publish"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
& $dotnet publish "$root\App" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $out -nologo
if ($LASTEXITCODE) { throw 'publish failed' }
foreach ($f in 'HistoricRoster.exe', 'USER_GUIDE.md', 'rosters\content_catalog.json', 'wpfgfx_cor3.dll', 'CspApp\HistoricRoster\HistoricRoster.lua') {
    if (-not (Test-Path "$out\$f")) { throw "publish output is missing $f" }
}
# The race database isn't part of the project; it comes from the rosters folder.
foreach ($db in @("$root\rosters\historic_rosters_db.json", "$root\dist\HistoricRoster\rosters\historic_rosters_db.json", "$root\HistoricRosterPlugin\rosters\historic_rosters_db.json")) {
    if ((Test-Path $db) -and (-not (Test-Path "$out\rosters\historic_rosters_db.json") -or (Get-Item $db).LastWriteTime -gt (Get-Item "$out\rosters\historic_rosters_db.json").LastWriteTime)) {
        Copy-Item $db "$out\rosters\historic_rosters_db.json" -Force
    }
}

& $makensis /V2 "/DVERSION=$version" "/DSOURCE=$out" "$PSScriptRoot\HistoricRoster.nsi"
if ($LASTEXITCODE) { throw 'makensis failed' }

# Keep the portable copy in dist\HistoricRoster current too (its own rosters folder and catalogs are kept).
$portable = "$root\dist\HistoricRoster"
New-Item -ItemType Directory -Force "$portable\rosters" | Out-Null
Get-ChildItem $out -File | Copy-Item -Destination $portable -Force
if (Test-Path "$portable\CspApp") { Remove-Item "$portable\CspApp" -Recurse -Force }
Copy-Item "$out\CspApp" "$portable\CspApp" -Recurse -Force
Copy-Item "$out\rosters\content_catalog.json" "$portable\rosters\" -Force
if (-not (Test-Path "$portable\rosters\historic_rosters_db.json")) { Copy-Item "$out\rosters\historic_rosters_db.json" "$portable\rosters\" }
Write-Host "Built $root\dist\HistoricRosterSetup.exe ($version)"
