# PawnOwnership 双客户端联机自动化测试（L3）
#
# 在同一台机器上启动两个 RimWorld 进程并自动完成联机：
#   1. 备份 MP 设置文件，串行写入不同用户名（MP 的用户名在启动时读取并缓存，运行中修改无效）
#   2. 起主机（readsave → 转联机主机），等待日志出现 "Server started."
#   3. 起客户端（DirectConnect），等待客户端产出 scenario-report.json
#   4. 汇总两端结果到 artifacts/mp-report.json，产物复制到 artifacts/runs/<时间>-mp-observe/
#   5. 强杀两个进程并恢复 MP 设置文件
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\harness\run-mp-tests.ps1 `
#       -SaveName '新移民1' -Port 30502 -HostTimeoutSec 240 -ClientTimeoutSec 300
#
# 注意：
#   - 两个进程共用同一份 Config（-savedatafolder 不能隔离配置），因此必须串行改用户名文件；
#   - 结束时强杀进程，避免两端并发写 Prefs/ModSettings 造成冲突。

[CmdletBinding()]
param(
    [string]$SaveName = '新移民1',
    [int]$Port = 30502,
    [int]$HostTimeoutSec = 240,
    [int]$ClientTimeoutSec = 300,
    [string]$GameExe = 'F:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64.exe',
    [string]$GameModPath = 'F:\SteamLibrary\steamapps\common\RimWorld\Mods\PawnOwnerShip',
    [string]$Scenario = 'mpobserve'
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$artifacts = Join-Path $repo 'artifacts'
if (-not (Test-Path $artifacts)) { New-Item -ItemType Directory -Force -Path $artifacts | Out-Null }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$work = Join-Path $env:TEMP ('po-mp-' + $stamp)
New-Item -ItemType Directory -Force -Path $work | Out-Null
$hostDir = Join-Path $work 'host'
$clientDir = Join-Path $work 'client'
New-Item -ItemType Directory -Force -Path $hostDir, $clientDir | Out-Null
$hostLog = Join-Path $work 'host.log'
$clientLog = Join-Path $work 'client.log'

# MP 的活动设置文件：不同版本下可能用 packageId（Mod_2606448745_Multiplayer.xml）
# 或 mod 名（Mod_Multiplayer_Multiplayer.xml）命名，两个都处理，避免改到过期文件。
$mpConfigDir = Join-Path $env:USERPROFILE 'AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config'
$mpConfigNames = @('Mod_Multiplayer_Multiplayer.xml', 'Mod_2606448745_Multiplayer.xml')
$mpConfigs = @($mpConfigNames | ForEach-Object { Join-Path $mpConfigDir $_ } | Where-Object { Test-Path $_ })

$script:steps = @()
$script:procs = @()

function Add-Step {
    param([string]$Name, [bool]$Ok, [string]$Detail)
    $script:steps += [ordered]@{ name = $Name; ok = $Ok; detail = $Detail }
    $mark = if ($Ok) { '[PASS]' } else { '[FAIL]' }
    Write-Host ("{0} {1}  {2}" -f $mark, $Name, $Detail)
}

function Set-MpUsername {
    param([string]$Name)
    if ($script:mpConfigs.Count -eq 0) { throw "找不到 MP 设置文件（已查找: $($script:mpConfigNames -join ',')）" }
    foreach ($cfg in $script:mpConfigs) {
        $c = Get-Content $cfg -Raw -Encoding UTF8
        $c2 = [regex]::Replace($c, '<username>[^<]*</username>', ('<username>' + $Name + '</username>'))
        [System.IO.File]::WriteAllText($cfg, $c2, (New-Object System.Text.UTF8Encoding($false)))
    }
}

function Stop-TestProcess {
    param($Proc)
    if ($Proc -and (Get-Process -Id $Proc.Id -ErrorAction SilentlyContinue)) {
        Stop-Process -Id $Proc.Id -Force -ErrorAction SilentlyContinue
    }
}

# ---- 窗口聚焦辅助 ----
# Unity 在窗口失去焦点时会降速/暂停（RimWorld 由 Prefs.RunInBackground 控制）。
# 自动化测试窗口若没有焦点，会出现"启动期假死"，必须手动点一下窗口才继续。
try {
    Add-Type -Namespace POWin -Name Focus -MemberDefinition @"
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
"@ -ErrorAction SilentlyContinue
} catch { }

function Focus-GameWindow {
    param($Proc, [int]$TimeoutSec = 30)
    if (-not $Proc) { return $false }
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        try {
            $Proc.Refresh()
            if ($Proc.HasExited) { return $false }
            $h = $Proc.MainWindowHandle
            if ($h -ne [IntPtr]::Zero) {
                try { $ws = New-Object -ComObject WScript.Shell; [void]$ws.AppActivate($Proc.Id) } catch { }
                try {
                    [void][POWin.Focus]::ShowWindow($h, 9)
                    [void][POWin.Focus]::BringWindowToTop($h)
                    [void][POWin.Focus]::SetForegroundWindow($h)
                } catch { }
                return $true
            }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

try {
    Write-Host "== PawnOwnership 联机门禁 =="
    Write-Host "repo = $repo"
    Write-Host "work = $work"

    if (-not (Test-Path $GameExe)) { throw "找不到游戏可执行文件: $GameExe" }
    if ($script:mpConfigs.Count -eq 0) { throw "找不到 Multiplayer 设置文件（已查找: $($script:mpConfigNames -join ',')）" }

    # 备份 MP 设置（只备份一次，保留用户原始值）
    foreach ($cfg in $script:mpConfigs) {
        $bak = "$cfg.po-harness-backup"
        if (-not (Test-Path $bak)) {
            Copy-Item $cfg $bak -Force
            Write-Host "已备份 MP 设置 -> $bak"
        }
    }
    $cfgNames = ($script:mpConfigs | ForEach-Object { Split-Path $_ -Leaf }) -join ','
    Add-Step 'backup-mp-settings' $true $cfgNames

    # 清理上次残留的测试进程（只杀带测试参数的）
    $leftovers = Get-CimInstance Win32_Process -Filter "Name='RimWorldWin64.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine -match 'po\.selftest' }
    foreach ($p in $leftovers) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    if ($leftovers) { Add-Step 'cleanup-leftovers' $true ("清理 " + @($leftovers).Count + " 个残留进程") }
    else { Add-Step 'cleanup-leftovers' $true '无残留进程' }

    # ---- 主机 ----
    Set-MpUsername 'MPHost'
    $hostArgs = '-logFile "{0}" -po.selftest-scenario={1} -po.selftest-out="{2}" -po.selftest-save="{3}" -po.selftest-mp=host' -f $hostLog, $Scenario, $hostDir, $SaveName
    $hostProc = Start-Process -FilePath $GameExe -ArgumentList $hostArgs -PassThru
    $script:procs += $hostProc
    $hostFocused = Focus-GameWindow -Proc $hostProc -TimeoutSec 30
    Add-Step 'host-focus' $hostFocused $(if ($hostFocused) { '已聚焦主机窗口' } else { '未取得焦点（依赖 RunInBackground 兜底）' })

    $hostReady = $false
    $deadline = (Get-Date).AddSeconds($HostTimeoutSec)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
        if (Select-String -Path $hostLog -Pattern 'Server started\.' -ErrorAction SilentlyContinue) { $hostReady = $true; break }
        if (-not (Get-Process -Id $hostProc.Id -ErrorAction SilentlyContinue)) { break }
    }

    if (-not $hostReady) {
        Add-Step 'host' $false "主机未在 $HostTimeoutSec 秒内启动服务器（日志: $hostLog）"
        throw '主机启动失败'
    }
    Add-Step 'host' $true 'Server started'

    # ---- 客户端 ----
    Set-MpUsername 'MPClient'
    $clientArgs = '-logFile "{0}" -po.selftest-scenario={1} -po.selftest-out="{2}" -po.selftest-mp=client:127.0.0.1:{3}' -f $clientLog, $Scenario, $clientDir, $Port
    $clientProc = Start-Process -FilePath $GameExe -ArgumentList $clientArgs -PassThru
    $script:procs += $clientProc
    $clientFocused = Focus-GameWindow -Proc $clientProc -TimeoutSec 30
    Add-Step 'client-focus' $clientFocused $(if ($clientFocused) { '已聚焦客户端窗口' } else { '未取得焦点（依赖 RunInBackground 兜底）' })

    $clientReport = Join-Path $clientDir 'scenario-report.json'
    $deadline = (Get-Date).AddSeconds($ClientTimeoutSec)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
        if (Test-Path $clientReport) { break }
        if (-not (Get-Process -Id $clientProc.Id -ErrorAction SilentlyContinue)) { break }
    }

    if (-not (Test-Path $clientReport)) {
        Add-Step 'client' $false "客户端未在 $ClientTimeoutSec 秒内产出报告（日志: $clientLog）"
        throw '客户端失败'
    }

    $cr = Get-Content $clientReport -Raw -Encoding UTF8 | ConvertFrom-Json
    $cOk = [bool]$cr.ok
    Add-Step 'client' $cOk ("ok=" + $cOk + "; " + $cr.error)
    foreach ($n in $cr.notes) { Write-Host ("        " + $n) }

    # ---- 汇总 ----
    $hostReport = Join-Path $hostDir 'scenario-report.json'
    $report = [ordered]@{
        stage       = 'L3'
        startedAt   = $stamp
        ok          = ($cOk -and $hostReady)
        scenario    = $Scenario
        save        = $SaveName
        port        = $Port
        host        = [ordered]@{ log = $hostLog; report = $hostReport; ready = $hostReady }
        client      = [ordered]@{ log = $clientLog; report = $clientReport; ok = $cOk; error = $cr.error; notes = @($cr.notes) }
        steps       = @($script:steps)
    }
    $reportPath = Join-Path $artifacts 'mp-report.json'
    $report | ConvertTo-Json -Depth 6 | Set-Content -Path $reportPath -Encoding UTF8

    # 产物归档（无论成败都复制）
    $runDir = Join-Path $artifacts ('runs\' + $stamp + '-mp-observe')
    New-Item -ItemType Directory -Force -Path $runDir | Out-Null
    foreach ($f in @($hostLog, $clientLog)) {
        if (Test-Path $f) { Copy-Item $f $runDir -Force }
    }
    if ($hostReport -and (Test-Path $hostReport)) { Copy-Item $hostReport (Join-Path $runDir 'host-report.json') -Force }
    if (Test-Path $clientReport) { Copy-Item $clientReport (Join-Path $runDir 'client-report.json') -Force }
    Copy-Item $reportPath $runDir -Force

    Write-Host ''
    if ($report.ok) { Write-Host '== 结果: PASS ==' } else { Write-Host '== 结果: FAIL ==' }
    Write-Host "报告: $reportPath"
    Write-Host "产物: $runDir"
}
catch {
    Add-Step 'exception' $false $_.Exception.Message
    $reportPath = Join-Path $artifacts 'mp-report.json'
    [ordered]@{
        stage = 'L3'; startedAt = $stamp; ok = $false; scenario = $Scenario; save = $SaveName
        steps = @($script:steps); error = $_.Exception.Message
    } | ConvertTo-Json -Depth 6 | Set-Content -Path $reportPath -Encoding UTF8

    $runDir = Join-Path $artifacts ('runs\' + $stamp + '-mp-observe')
    New-Item -ItemType Directory -Force -Path $runDir | Out-Null
    foreach ($f in @($hostLog, $clientLog)) { if (Test-Path $f) { Copy-Item $f $runDir -Force } }
    Copy-Item $reportPath $runDir -Force -ErrorAction SilentlyContinue
    Write-Host ''
    Write-Host '== 结果: FAIL =='
    Write-Host "报告: $reportPath"
    exit 1
}
finally {
    foreach ($p in $script:procs) { Stop-TestProcess $p }
    Start-Sleep -Milliseconds 800
    # Prepatcher 会重启游戏进程（新 PID），按命令行兜底清理，避免残留锁住 DLL
    $leftovers = Get-CimInstance Win32_Process -Filter "Name='RimWorldWin64.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine -match 'po\.selftest' }
    foreach ($lp in $leftovers) { Stop-Process -Id $lp.ProcessId -Force -ErrorAction SilentlyContinue }
    foreach ($cfg in $script:mpConfigs) {
        $bak = "$cfg.po-harness-backup"
        if (Test-Path $bak) { Copy-Item $bak $cfg -Force }
    }
}