param([string]$Filter = '')
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.cache\dotnet'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.cache\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:HUB_TEST_FILTER = $Filter
try {
    & (Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe') run --project (Join-Path $PSScriptRoot 'tests\StartupHub.Tests.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw '验收失败，请查看 artifacts 中的 results.json。' }
} finally { Remove-Item Env:HUB_TEST_FILTER -ErrorAction SilentlyContinue }
