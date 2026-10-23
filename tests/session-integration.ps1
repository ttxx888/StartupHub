param([string]$AppDir = (Join-Path $PSScriptRoot '..\artifacts\preview-task-management\app'))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$data = Join-Path $root ('artifacts\session-cli-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $data | Out-Null
$cli = Join-Path $AppDir 'startuphub-cli.exe'
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
function Exit-Hub {
    $state = Invoke-Hub @('ui','state')
    $process = [Diagnostics.Process]::GetProcessById($state.Pid)
    Invoke-Hub @('ui','exit') | Out-Null
    if (!$process.WaitForExit(10000)) { throw '测试启动台未退出' }
}
try {
    $shell = New-Object -ComObject WScript.Shell
    $linkPath = Join-Path $data '打开验证.lnk'
    $link = $shell.CreateShortcut($linkPath)
    $link.TargetPath = $env:ComSpec; $link.Arguments = '/c exit 0'; $link.WindowStyle = 7; $link.Save()
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) | Out-Null
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
    $taskFile = Join-Path $data 'task.json'
    @{ Name='会话文件'; Kind='File'; Path=$linkPath; AutoStart=$false } | ConvertTo-Json | Set-Content -LiteralPath $taskFile -Encoding utf8
    $file = Invoke-Hub @('tasks','add','--file',$taskFile)
    $opened = Invoke-Hub @('run','open',$file.Id)
    Assert-Hub ($opened.Status -eq 'Opened') '真实 CLI 打开文件后立即返回已打开'
    $before = Invoke-Hub @('ui','show')
    Invoke-Hub @('ui','select',$file.Id) | Out-Null
    Invoke-Hub @('ui','hide') | Out-Null
    $shown = Invoke-Hub @('ui','show')
    $rows = @(Invoke-Hub @('tasks','list'))
    Assert-Hub ($shown.Pid -eq $before.Pid -and $rows[0].Status.Status -eq 'Opened') '托盘隐藏恢复和 CLI 刷新保留同一会话状态'
    $history = Get-Content -LiteralPath (Join-Path $data 'state.json') -Raw | ConvertFrom-Json
    Assert-Hub ($history.Runs.($file.Id).Status -eq 'Opened') '打开历史已保存'
    Exit-Hub
    $fresh = @(Invoke-Hub @('tasks','list'))
    Assert-Hub ($fresh[0].Status.Status -eq 'Stopped' -and $fresh[0].Status.Detail -eq '可直接打开') '完整退出并由 CLI 重启后状态重置'
    $history = Get-Content -LiteralPath (Join-Path $data 'state.json') -Raw | ConvertFrom-Json
    Assert-Hub ($history.Runs.($file.Id).Status -eq 'Opened') '重启未清除历史记录'
    @{ Passed=$checks.Count; Checks=$checks } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $data 'results.json') -Encoding utf8
    Write-Host "RESULT $($checks.Count) passed: $data"
}
finally { Exit-Hub }
