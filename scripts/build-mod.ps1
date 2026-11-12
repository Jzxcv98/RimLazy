param([string]$GameDir = $env:RIMWORLD_DIR)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $GameDir -or -not (Test-Path -LiteralPath (Join-Path $GameDir 'Assembly-CSharp.dll') -PathType Leaf)) {
    throw 'Pass -GameDir pointing to your RimWorld 1.7 install, or set RIMWORLD_DIR.'
}
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
if ((Get-Content -LiteralPath (Join-Path $GameDir 'Version.txt') -Raw).Trim() -notmatch '^1\.7\.') { throw 'Expected RimWorld 1.7.' }
$dotnet = @((Join-Path $env:LOCALAPPDATA 'RimLazy/dotnet/dotnet.exe'), (Join-Path $env:USERPROFILE 'AppData/Local/RimLazy/dotnet/dotnet.exe')) |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $dotnet) { $dotnet = 'dotnet' }
$oldHome = $env:DOTNET_CLI_HOME
Push-Location -LiteralPath $repo
try {
    if (-not $oldHome) { $env:DOTNET_CLI_HOME = Join-Path $repo '.local/dotnet-cli' }
    & $dotnet build src/StrategicWork.RimWorld/StrategicWork.RimWorld.csproj -c Release "-p:RimWorldDir=$GameDir" --disable-build-servers '-m:1' --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Game adapter build failed.' }
    $dllDir = 'src/StrategicWork.RimWorld/bin/Release/net10.0'
    $dist = Join-Path $repo '.artifacts'
    $stage = Join-Path $dist ('stage-' + [guid]::NewGuid().ToString('N'))
    $payload = Join-Path $stage 'RimLazy'
    New-Item -Path $stage -ItemType Directory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo 'Mod') -Destination $payload -Recurse
    $assemblies = New-Item -ItemType Directory -Path (Join-Path $payload 'Assemblies')
    foreach ($name in @('StrategicWork.Core.dll', 'StrategicWork.RimWorld.dll')) {
        Copy-Item -LiteralPath (Join-Path $dllDir $name) -Destination $assemblies.FullName
    }
    Get-ChildItem -LiteralPath $payload -Filter '*.xml' -Recurse | ForEach-Object { [void][xml](Get-Content -Raw -Encoding UTF8 -LiteralPath $_.FullName) }
    $about = [xml](Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $payload 'About/About.xml'))
    $zip = Join-Path $dist ('RimLazy-' + $about.ModMetaData.modVersion + '.zip')
    # includeBaseDirectory preserves RimLazy/ as the single top-level folder when unpacking into Mods.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $tmpZip = Join-Path $stage 'release.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $tmpZip, [IO.Compression.CompressionLevel]::Optimal, $true)
    Copy-Item -LiteralPath $tmpZip -Destination $zip -Force
    Write-Host "Mod directory: $payload"
    Write-Host "Installable archive: $zip"
} finally {
    $env:DOTNET_CLI_HOME = $oldHome
    Pop-Location
}
