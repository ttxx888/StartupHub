param([string]$AppDir = (Join-Path $PSScriptRoot '..\artifacts\preview-task-management\app'), [switch]$UiChecks)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$data = Join-Path $root ('artifacts\discovery-cli-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $data | Out-Null
$cli = Join-Path $AppDir 'startuphub-cli.exe'
$fixtureExe = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\StartupHub.Tests.exe'
$checks = [Collections.Generic.List[string]]::new()
function Invoke-Hub([string[]]$Command) {
    $raw = & $cli --data $data --json @Command
    $result = $raw | ConvertFrom-Json
    if (!$result.ok) { throw ($Command -join ' ') + ': ' + $result.error }
    return $result.data
}
function Assert-Hub($Condition, [string]$Name) {
    if (!$Condition) { throw $Name }
    $checks.Add($Name); Write-Host "PASS $Name"
}
$fixtureProcess = Start-Process -FilePath $fixtureExe -ArgumentList ('--discover-serve "' + $data + '"') -WindowStyle Hidden -PassThru
try {
    $fixtureFile = Join-Path $data 'fixture.json'
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    while (!(Test-Path -LiteralPath $fixtureFile) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
    $ports = Get-Content -LiteralPath $fixtureFile -Raw | ConvertFrom-Json
    $pages = @(Invoke-Hub @('discover'))
    $sample = @($pages | Where-Object { $_.Port -in @($ports.Html,$ports.Xhtml,$ports.Json,$ports.Error,$ports.Empty,$ports.Redirect) })
    $sample | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $data 'sample-results.json') -Encoding utf8
    Assert-Hub ($sample.Count -eq 2 -and $ports.Html -in $sample.Port -and $ports.Xhtml -in $sample.Port) '发布版 CLI 仅保留 HTML 与 XHTML 并合并重定向'
    Assert-Hub (($sample | Where-Object Port -eq $ports.Html).Name -eq '验收网页' -and @($sample | Where-Object {$_.ProcessName -ne 'StartupHub.Tests'}).Count -eq 0) 'CLI 返回网页标题及实际所属程序'
    $taskFile = Join-Path $data 'known.json'
    @{Name='已有网址';Kind='Url';Url="http://localhost:$($ports.Html)/";Port=0;AutoStart=$false} | ConvertTo-Json | Set-Content -LiteralPath $taskFile -Encoding utf8
    Invoke-Hub @('tasks','add','--file',$taskFile) | Out-Null
    $again = @(Invoke-Hub @('discover'))
    Assert-Hub ($ports.Html -notin $again.Port -and $ports.Redirect -notin $again.Port -and $ports.Xhtml -in $again.Port) '已有网址未手填端口时仍排除本地入口和跳转来源'
    if ($UiChecks) {
        $env:DOTNET_CLI_HOME = Join-Path $root '.cache\dotnet'
        $env:NUGET_PACKAGES = Join-Path $root '.cache\nuget'
        & (Join-Path $root '.tools\dotnet\dotnet.exe') run --project (Join-Path $root 'ui-tests\StartupHub.UiTests.csproj') -c Release -- --discover-live $fixtureFile
        if ($LASTEXITCODE -ne 0) { throw 'WPF 实际扫描与添加检查失败' }
    }
    @{Passed=$checks.Count;Checks=$checks} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $data 'results.json') -Encoding utf8
    Write-Host "RESULT $($checks.Count) passed: $data"
}
finally {
    if (!$fixtureProcess.HasExited) { $fixtureProcess.Kill(); $fixtureProcess.WaitForExit() }
    $state = Invoke-Hub @('ui','state')
    $process = [Diagnostics.Process]::GetProcessById($state.Pid)
    Invoke-Hub @('ui','exit') | Out-Null
    if (!$process.WaitForExit(10000)) { throw '测试启动台未退出' }
}
