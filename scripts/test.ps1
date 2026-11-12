$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$userSdk = @(
    (Join-Path $env:LOCALAPPDATA 'RimLazy/dotnet/dotnet.exe')
    (Join-Path $env:USERPROFILE 'AppData/Local/RimLazy/dotnet/dotnet.exe')
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
$dotnetExe = if ($userSdk) { $userSdk } else { 'dotnet' }
$previousCliHome = $env:DOTNET_CLI_HOME
$previousCert = $env:DOTNET_GENERATE_ASPNET_CERTIFICATE
$previousToolsPath = $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH
Push-Location -LiteralPath $repoRoot
try {
    if (-not $previousCliHome) { $env:DOTNET_CLI_HOME = Join-Path $repoRoot '.local/dotnet-cli' }
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    & $dotnetExe test RimLazy.slnx --configuration Release --nologo @args
    $testExit = $LASTEXITCODE
} finally {
    $env:DOTNET_CLI_HOME = $previousCliHome
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = $previousCert
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = $previousToolsPath
    Pop-Location
}
exit $testExit
