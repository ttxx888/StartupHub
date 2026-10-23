param([string]$AppDir = (Join-Path $PSScriptRoot '..\artifacts\preview-app'), [switch]$LoginOnly)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$data = Join-Path $root ('artifacts\cli-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $data -Force | Out-Null
$cli = Join-Path $AppDir 'startuphub-cli.exe'
$exe = Join-Path $AppDir 'StartupHub.exe'
$fixture = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\StartupHub.Tests.exe'
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
function Write-Json($Value, $Path) { $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding utf8 }
$service = $null
$taskFile = Join-Path $data 'task.json'
try {
    if (!$LoginOnly) {
    $state = Invoke-Hub @('ui','state')
    Assert-Hub (!$state.Visible -and $state.VisibleTasks.Count -eq 0) 'CLI 首次连接仅启动托盘'
    $same = Invoke-Hub @('ui','state')
    Assert-Hub ($same.Pid -eq $state.Pid) '多次连接保持同一实例'
    Invoke-Hub @('groups','add','验收分类') | Out-Null
    Invoke-Hub @('groups','rename','验收分类','临时任务') | Out-Null
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
    $taskFile = Join-Path $data 'task.json'
    Write-Json @{ Name='CLI 验收服务'; Group='临时任务'; Kind='Service'; Path=$fixture; Arguments=@('--serve',"$port"); WorkingDir=$data; Port=$port; CheckUrl="http://127.0.0.1:$port/"; WaitSeconds=8 } $taskFile
    $service = Invoke-Hub @('tasks','add','--file',$taskFile)
    $patch = Join-Path $data 'patch.json'; Write-Json @{Info='中文命令验收'} $patch
    Invoke-Hub @('tasks','update',$service.Id,'--file',$patch) | Out-Null
    $copy = Invoke-Hub @('tasks','copy',$service.Id,'--name','临时副本')
    Invoke-Hub @('tasks','move',$copy.Id,'up') | Out-Null
    Invoke-Hub @('tasks','autostart',$copy.Id,'on') | Out-Null
    Assert-Hub ((Invoke-Hub @('tasks','show',$copy.Id)).AutoStart) '任务添加、修改、复制、排序与自启编辑'
    Invoke-Hub @('tasks','delete',$copy.Id) | Out-Null
    $export = Join-Path $data 'export.json'
    Invoke-Hub @('config','export',$export) | Out-Null
    Invoke-Hub @('config','import',$export) | Out-Null
    Assert-Hub ((Invoke-Hub @('tasks','show',$service.Id)).Info -eq '中文命令验收') '配置导出导入保留 ID 和字段'
    $running = Invoke-Hub @('run','start',$service.Id,'--background')
    Assert-Hub ($running.Status -eq 'Running') 'CLI 启动服务并等待就绪'
    $again = Invoke-Hub @('run','start',$service.Id,'--background')
    Assert-Hub ($again.Pid -eq $running.Pid) '重复启动复用原进程'
    Start-Sleep -Milliseconds 350
    $log = Invoke-Hub @('logs','show',$service.Id)
    Assert-Hub ($log -match '服务心跳') 'CLI 读取服务输出'
    Invoke-Hub @('ui','exit') | Out-Null
    Start-Sleep -Milliseconds 600
    Assert-Hub ($null -ne (Get-Process -Id $running.Pid -ErrorAction SilentlyContinue)) '退出管理器后服务继续运行'
    $status = Invoke-Hub @('run','status')
    Assert-Hub ($status[0].Status.Status -eq 'Running') '重开管理器识别原服务'
    $log2 = Invoke-Hub @('logs','show',$service.Id)
    Assert-Hub ($log2.Length -gt $log.Length) '退出期间日志持续写入'
    $restart = Invoke-Hub @('run','restart',$service.Id)
    Assert-Hub ($restart.Status -eq 'Running' -and $restart.Pid -ne $running.Pid) 'CLI 重启产生新服务进程'
    Invoke-Hub @('run','stop',$service.Id) | Out-Null
    Assert-Hub (!(Get-Process -Id $restart.Pid -ErrorAction SilentlyContinue)) 'CLI 停止所管理服务'
    Invoke-Hub @('tasks','delete',$service.Id) | Out-Null
    $service = $null
    Invoke-Hub @('groups','delete','临时任务') | Out-Null
    }
    $marker = Join-Path $data 'login.txt'; $script = Join-Path $data 'login.ps1'
    [IO.File]::WriteAllText($script, "Add-Content -LiteralPath '$marker' -Value 'run'", [Text.UTF8Encoding]::new($true))
    Write-Json @{Name='登录验收'; Kind='Script'; Path=$script; AutoStart=$true} $taskFile
    Invoke-Hub @('tasks','add','--file',$taskFile) | Out-Null
    Assert-Hub (!(Test-Path -LiteralPath $marker)) '编辑自启清单不会立即执行'
    Invoke-Hub @('ui','exit') | Out-Null
    Start-Sleep -Milliseconds 300
    $startArgs = '--autostart --tray --data "' + $data + '"'
    Start-Process -FilePath $exe -ArgumentList $startArgs -WindowStyle Hidden
    for ($i=0; $i -lt 50 -and !(Test-Path -LiteralPath $marker); $i++) { Start-Sleep -Milliseconds 200 }
    Assert-Hub ((Get-Content -LiteralPath $marker).Count -eq 1) '登录参数执行自启脚本一次'
    Start-Process -FilePath $exe -ArgumentList $startArgs -WindowStyle Hidden -Wait
    Start-Sleep -Milliseconds 400
    Assert-Hub ((Get-Content -LiteralPath $marker).Count -eq 1) '重复登录调用不创建第二队列'
    Invoke-Hub @('ui','exit') | Out-Null
    Start-Sleep -Milliseconds 300
    Invoke-Hub @('ui','state') | Out-Null
    Start-Sleep -Milliseconds 300
    Assert-Hub ((Get-Content -LiteralPath $marker).Count -eq 1) '普通启动不执行已勾选清单'
    $errorResult = (& $cli --data $data --json unknown-command) | ConvertFrom-Json
    Assert-Hub (!$errorResult.ok -and $LASTEXITCODE -ne 0) '未知命令返回 JSON 错误和非零退出码'
    Write-Json @{Passed=$checks.Count; Checks=$checks} (Join-Path $data 'results.json')
    Write-Host "RESULT $($checks.Count) passed: $data"
} finally {
    if ($service) { Invoke-Hub @('run','stop',$service.Id) | Out-Null }
    Invoke-Hub @('ui','exit') | Out-Null
}


