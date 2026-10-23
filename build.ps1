param([string]$Output = (Join-Path $PSScriptRoot 'app'))
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.cache\dotnet'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.cache\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$dotnet = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
foreach ($project in @('StartupHub.App','StartupHub.Cli')) {
    & $dotnet publish (Join-Path $PSScriptRoot "src\$project\$project.csproj") -c Release -o $Output
    if ($LASTEXITCODE -ne 0) { throw "构建失败：$project" }
}
Write-Host "程序目录：$Output"
