<#
.SYNOPSIS
    PawnOwnership 门禁脚本（L0 静态门禁 + 可选部署）。

.DESCRIPTION
    一次运行完成：
      1. 编译 Mod（中间产物与输出重定向到临时工作目录，不污染仓库）
      2. 编译 tools/PatchAudit
      3. 用 PatchAudit 校验所有 Harmony 补丁的绑定
      4. 可选：把 DLL 部署到游戏 Mods 目录
      5. 把结果写入 <repo>/artifacts/gate-report.json

    注意：临时目录路径在单次运行内是稳定的。跨进程调用时 %TEMP% 可能变化，
    因此所有步骤必须在同一次运行内完成，报告写回仓库而不是临时目录。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\harness\run-tests.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\harness\run-tests.ps1 -Deploy
#>
[CmdletBinding()]
param(
    # 编译通过后把 DLL/PDB 复制到游戏 Mods 目录（需要写 F:\ 的权限）
    [switch]$Deploy,

    # 游戏侧 Mod 目录
    [string]$GameModPath = 'F:\SteamLibrary\steamapps\common\RimWorld\Mods\PawnOwnerShip',

    # 启动游戏跑 L1 冒烟（需要先 -Deploy，且能写 F:\ 与启动进程）
    [switch]$Launch,

    [string]$GameExe = 'F:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64.exe',

    [int]$LaunchTimeoutSec = 150,

    # L2 场景驱动：quickstart（开发者模式快速开局）、quicktest（Root_Play 快速测试开局）、
    # loadsave（加载指定存档）/ ownership（跑归属用例）/ cropprobe（作物生效性）/ markerprobe（标记可见性）
    [string]$Scenario = '',

    [string]$SaveName = ''
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo      = (Resolve-Path (Join-Path $scriptDir '..\..')).Path
$artifacts = Join-Path $repo 'artifacts'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$work  = Join-Path $env:TEMP ("po-gate-" + $stamp + "-" + ([Guid]::NewGuid().ToString('N')))
New-Item -ItemType Directory -Force -Path $work | Out-Null

$steps = New-Object System.Collections.ArrayList
$selfTest       = $null
$scenarioReport = $null
$modDll         = $null

function Add-Step {
    param([string]$Name, [bool]$Ok, [string]$Detail)
    [void]$steps.Add([ordered]@{ name = $Name; ok = $Ok; detail = $Detail })
    $mark = if ($Ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("[{0}] {1}  {2}" -f $mark, $Name, $Detail)
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

# ownership / cropprobe 场景的逐用例结果在 <scenarioDir>/<report>.json（由 mod 侧用例类写出）。
# 这里只负责把 total/passed/failed 汇总到 scenario 步骤的 detail；读不到就用说明文字兜底，
# 不让展示逻辑抛异常（是否通过与 scenario-report.json 的 ok 字段收口）。
function Get-CaseSummary {
    param([string]$ScenarioDir, [string]$ReportName, [string]$Label)
    $path = Join-Path $ScenarioDir $ReportName
    if (-not (Test-Path $path)) { return ($Label + ": 缺少 " + $ReportName) }
    try {
        $r = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
        return ($Label + ": total=" + $r.total + " passed=" + $r.passed + " failed=" + $r.failed)
    }
    catch {
        return ($Label + ": " + $ReportName + " 解析失败 - " + $_.Exception.Message)
    }
}

function Write-GateReport {
    $failed  = @($steps | Where-Object { -not $_.ok })
    $overall = ($failed.Count -eq 0)

    $report = [ordered]@{
        stage     = $(if ($Scenario) { 'L2' } elseif ($Launch) { 'L1' } else { 'L0' })
        startedAt = $stamp
        ok        = $overall
        steps     = @($steps)
        dll       = $modDll
        selfTest  = $selfTest
        scenario  = $scenarioReport
    }
    $reportPath = Join-Path $artifacts 'gate-report.json'
    $report | ConvertTo-Json -Depth 6 | Set-Content -Path $reportPath -Encoding UTF8

    Write-Host ""
    Write-Host ("== 结果: " + $(if ($overall) { 'PASS' } else { 'FAIL' }) + " ==")
    Write-Host "报告: $reportPath"

    return $overall
}

Write-Host "== PawnOwnership 门禁 =="
Write-Host "repo = $repo"
Write-Host "work = $work"
Write-Host ""

# ---------------------------------------------------------------- 0. 参数验证
if ($Scenario) { $Scenario = $Scenario.ToLowerInvariant() }

$inputErrors = @()

if ($Scenario -and -not $Launch) {
    $inputErrors += "指定 -Scenario 时必须同时指定 -Launch"
}
if ($Launch -and -not $Deploy) {
    $inputErrors += "指定 -Launch 时必须同时指定 -Deploy"
}
if ($Scenario -notin @('', 'quickstart', 'loadsave', 'quicktest', 'ownership', 'cropprobe', 'markerprobe')) {
    $inputErrors += ("-Scenario 只允许 ''/quickstart/loadsave/quicktest/ownership/cropprobe/markerprobe（当前: " + $Scenario + "）")
}
if ($Scenario -eq 'loadsave' -and [string]::IsNullOrWhiteSpace($SaveName)) {
    $inputErrors += "Scenario=loadsave 时必须提供 -SaveName"
}
if ($LaunchTimeoutSec -le 0) {
    $inputErrors += ("-LaunchTimeoutSec 必须大于 0（当前: " + $LaunchTimeoutSec + "）")
}

if ($inputErrors.Count -gt 0) {
    foreach ($e in $inputErrors) { Add-Step 'arguments' $false $e }
    [void](Write-GateReport)
    exit 1
}

# ---------------------------------------------------------------- 1. 编译 Mod
try {
    $auditOk    = $false
    $deployedOk = $false

    $modObj = Join-Path $work 'mod-obj'
    $modBin = Join-Path $work 'mod-bin'
    $modProj = Join-Path $repo 'Source\PawnOwnership.csproj'

    $buildOut = & dotnet build $modProj -v q --nologo `
        "-p:BaseIntermediateOutputPath=$modObj\" "-p:BaseOutputPath=$modBin\" "-p:OutputPath=$modBin\" 2>&1
    $buildExit = $LASTEXITCODE
    $buildLog  = Join-Path $artifacts ("build-" + $stamp + ".log")
    $buildOut | Set-Content -Path $buildLog -Encoding UTF8

    $modDll = Join-Path $modBin 'PawnOwnership.dll'
    $buildOk = ($buildExit -eq 0) -and (Test-Path $modDll)
    Add-Step 'build-mod' $buildOk ("exit=" + $buildExit + "; log=" + (Split-Path -Leaf $buildLog))

    if (-not $buildOk) {
        Write-Host ""
        Write-Host "编译失败，后续步骤跳过。完整日志：$buildLog"
        $buildOut | Select-Object -Last 30 | ForEach-Object { Write-Host $_ }
    }

    # ------------------------------------------------------- 2. 编译补丁审计工具
    if ($buildOk) {
        $auditObj = Join-Path $work 'audit-obj'
        $auditBin = Join-Path $work 'audit-bin'
        $auditProj = Join-Path $repo 'tools\PatchAudit\PatchAudit.csproj'

        $auditBuildOut = & dotnet build $auditProj -v q --nologo `
            "-p:BaseIntermediateOutputPath=$auditObj\" "-p:BaseOutputPath=$auditBin\" `
            "-p:OutputPath=$auditBin\" "-p:AppendTargetFrameworkToOutputPath=false" 2>&1
        $auditBuildExit = $LASTEXITCODE
        $auditExe = Join-Path $auditBin 'PatchAudit.exe'
        $auditBuilt = ($auditBuildExit -eq 0) -and (Test-Path $auditExe)
        Add-Step 'build-audit-tool' $auditBuilt ("exit=" + $auditBuildExit)

        # ------------------------------------------------------ 3. 补丁绑定审计
        if ($auditBuilt) {
            $libs = Join-Path $repo 'Source\Libs'
            $auditOut = & $auditExe $libs $modDll 2>&1
            $auditExit = $LASTEXITCODE
            $auditText = ($auditOut | Out-String)
            $auditLog = Join-Path $artifacts ("patch-audit-" + $stamp + ".log")
            $auditText | Set-Content -Path $auditLog -Encoding UTF8

            $okCount = 0; $failCount = -1
            if ($auditText -match 'RESULT ok=(\d+) fail=(\d+)') {
                $okCount   = [int]$Matches[1]
                $failCount = [int]$Matches[2]
            }

            $auditOk = ($auditExit -eq 0) -and ($failCount -eq 0) -and ($okCount -eq 31)
            Add-Step 'patch-audit' $auditOk ("ok=" + $okCount + " fail=" + $failCount + "; log=" + (Split-Path -Leaf $auditLog))

            if (-not $auditOk) {
                $auditOut | Where-Object { $_ -match 'FAIL|目标重载|需要绑定|补丁参数' } | ForEach-Object { Write-Host $_ }
            }
        }
    }

    # ---------------------------------------------------------------- 4. 部署
    if ($Deploy) {
        if (-not ($buildOk -and $auditOk)) {
            Add-Step 'deploy' $false 'L0未通过，阻止部署'
        }
        else {
            try {
                $dst = Join-Path $GameModPath '1.6\Assemblies'
                if (-not (Test-Path $dst)) { throw "目标目录不存在: $dst" }
                Copy-Item (Join-Path $modBin 'PawnOwnership.dll') $dst -Force
                $pdb = Join-Path $modBin 'PawnOwnership.pdb'
                if (Test-Path $pdb) { Copy-Item $pdb $dst -Force }
                $deployedOk = $true
                $deployed = Get-Item (Join-Path $dst 'PawnOwnership.dll')
                Add-Step 'deploy' $true ("-> " + $dst + " (" + $deployed.LastWriteTime + ")")
            }
            catch {
                $deployedOk = $false
                Add-Step 'deploy' $false $_.Exception.Message
            }
        }
    }

    # ------------------------------------- 4b. L1 启动冒烟 / L2 场景驱动
    if ($Launch) {
        $running = Get-Process -Name 'RimWorldWin64' -ErrorAction SilentlyContinue
        if (-not $deployedOk) {
            Add-Step 'launch' $false '部署未成功，阻止启动'
        }
        elseif ($running) {
            Add-Step 'launch' $false '游戏已在运行，跳过（请先关闭游戏）'
        }
        elseif (-not (Test-Path $GameExe)) {
            Add-Step 'launch' $false ("找不到游戏 exe: " + $GameExe)
        }
        else {
            $selfTestFile = Join-Path $work 'selftest.json'
            $scenarioDir  = Join-Path $work 'scenario'
            $scenarioFile = Join-Path $scenarioDir 'scenario-report.json'
            $gameLog      = Join-Path $work 'game.log'

            $argLine  = '-logFile "{0}" -po.selftest-report="{1}"' -f $gameLog, $selfTestFile
            $waitFile = $selfTestFile

            if ($Scenario) {
                New-Item -ItemType Directory -Force -Path $scenarioDir | Out-Null
                $argLine += ' -po.selftest-scenario={0} -po.selftest-out="{1}"' -f $Scenario, $scenarioDir
                if ($SaveName) { $argLine += ' -po.selftest-save="{0}"' -f $SaveName }
                $waitFile = $scenarioFile
            }

            # 用普通窗口启动并主动聚焦：隐藏/失焦的窗口会让 Unity 降速甚至暂停，
            # 表现为"启动期假死"（必须手动点窗口才继续）。
            $proc = Start-Process -FilePath $GameExe -PassThru -ArgumentList $argLine -WindowStyle Normal
            $focused = Focus-GameWindow -Proc $proc -TimeoutSec 30
            Write-Host ("focus: " + $(if ($focused) { '已聚焦游戏窗口' } else { '未能获取窗口焦点（依赖 mod 侧 RunInBackground 兜底）' }))

            $deadline = (Get-Date).AddSeconds($LaunchTimeoutSec)
            while ((Get-Date) -lt $deadline) {
                if (Test-Path $waitFile) { break }
                if ($proc.HasExited) { break }
                Start-Sleep -Milliseconds 500
            }

            $got = Test-Path $waitFile
            if (-not $proc.HasExited) {
                try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop } catch { }
            }

            # 保留游戏日志便于事后排查
            $gameLogCopy = Join-Path $artifacts ("game-" + $stamp + ".log")
            if (Test-Path $gameLog) { Copy-Item $gameLog $gameLogCopy -Force }

            # L1：补丁校验报告（只要 Mod 加载成功就会生成）
            if (Test-Path $selfTestFile) {
                try {
                    $selfTest = Get-Content $selfTestFile -Raw -Encoding UTF8 | ConvertFrom-Json
                    Add-Step 'launch-selfcheck' ([bool]$selfTest.healthy) ("patchedMethods=" + $selfTest.patchedMethods +
                        " prefix=" + $selfTest.prefixes + " postfix=" + $selfTest.postfixes +
                        " transpiler=" + $selfTest.transpilers)
                }
                catch {
                    Add-Step 'launch-selfcheck' $false ("自检报告解析失败: " + $_.Exception.Message)
                }
            }
            else {
                Add-Step 'launch-selfcheck' $false ("未生成补丁自检报告。日志: " + (Split-Path -Leaf $gameLogCopy))
            }

            # L2：场景驱动报告 + 截图
            # 无论成功与否，先把场景目录里的所有产物（报告 / 截图 / 崩溃标记）拷出来，
            # 否则进程一旦崩溃、临时目录又被回收，就什么证据都不剩。
            if ($Scenario) {
                $pngCount = 0
                if (Test-Path $scenarioDir) {
                    Get-ChildItem $scenarioDir -File -ErrorAction SilentlyContinue | ForEach-Object {
                        Copy-Item $_.FullName (Join-Path $artifacts ($stamp + '-' + $_.Name)) -Force
                        if ($_.Extension -eq '.png') { $pngCount++ }
                    }
                }

                if (Test-Path $scenarioFile) {
                    try {
                        $scenarioReport = Get-Content $scenarioFile -Raw -Encoding UTF8 | ConvertFrom-Json
                        $sOk = [bool]$scenarioReport.ok
                        if ($Scenario -eq 'ownership') {
                            $detail = Get-CaseSummary $scenarioDir 'ownership-report.json' 'ownership'
                        }
                        elseif ($Scenario -eq 'cropprobe') {
                            $detail = Get-CaseSummary $scenarioDir 'crop-report.json' 'crop'
                        }
                        else {
                            $detail = "ownedThings=" + $scenarioReport.ownedThings
                        }
                        $detail += " png=" + $pngCount
                        if (-not $sOk) { $detail += "; error=" + $scenarioReport.error }
                        Add-Step 'scenario' $sOk $detail
                    }
                    catch {
                        Add-Step 'scenario' $false ("场景报告解析失败: " + $_.Exception.Message)
                    }
                }
                else {
                    Add-Step 'scenario' $false ("未生成场景报告（png=" + $pngCount + "）。日志: " + (Split-Path -Leaf $gameLogCopy))
                }
            }
        }
    }

}
catch {
    Add-Step 'execution' $false $_.Exception.Message
}

# ---------------------------------------------------------------- 5. 报告
if (Write-GateReport) { exit 0 } else { exit 1 }
