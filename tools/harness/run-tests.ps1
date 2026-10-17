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

    [int]$LaunchTimeoutSec = 150
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo      = (Resolve-Path (Join-Path $scriptDir '..\..')).Path
$artifacts = Join-Path $repo 'artifacts'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$work  = Join-Path $env:TEMP ("po-gate-" + $stamp)
New-Item -ItemType Directory -Force -Path $work | Out-Null

$steps = New-Object System.Collections.ArrayList

function Add-Step {
    param([string]$Name, [bool]$Ok, [string]$Detail)
    [void]$steps.Add([ordered]@{ name = $Name; ok = $Ok; detail = $Detail })
    $mark = if ($Ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("[{0}] {1}  {2}" -f $mark, $Name, $Detail)
}

Write-Host "== PawnOwnership 门禁 =="
Write-Host "repo = $repo"
Write-Host "work = $work"
Write-Host ""

# ---------------------------------------------------------------- 1. 编译 Mod
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

        $auditOk = ($auditExit -eq 0) -and ($failCount -eq 0)
        Add-Step 'patch-audit' $auditOk ("ok=" + $okCount + " fail=" + $failCount + "; log=" + (Split-Path -Leaf $auditLog))

        if (-not $auditOk) {
            $auditOut | Where-Object { $_ -match 'FAIL|目标重载|需要绑定|补丁参数' } | ForEach-Object { Write-Host $_ }
        }
    }
}

# ---------------------------------------------------------------- 4. 部署
if ($buildOk -and $Deploy) {
    try {
        $dst = Join-Path $GameModPath '1.6\Assemblies'
        if (-not (Test-Path $dst)) { throw "目标目录不存在: $dst" }
        Copy-Item (Join-Path $modBin 'PawnOwnership.dll') $dst -Force
        $pdb = Join-Path $modBin 'PawnOwnership.pdb'
        if (Test-Path $pdb) { Copy-Item $pdb $dst -Force }
        $deployed = Get-Item (Join-Path $dst 'PawnOwnership.dll')
        Add-Step 'deploy' $true ("-> " + $dst + " (" + $deployed.LastWriteTime + ")")
    }
    catch {
        Add-Step 'deploy' $false $_.Exception.Message
    }
}

# ---------------------------------------------------------------- 4b. L1 启动冒烟
$selfTest = $null
if ($buildOk -and $Launch) {
    $running = Get-Process -Name 'RimWorldWin64' -ErrorAction SilentlyContinue
    if ($running) {
        Add-Step 'launch' $false '游戏已在运行，跳过（请先关闭游戏）'
    }
    elseif (-not (Test-Path $GameExe)) {
        Add-Step 'launch' $false ("找不到游戏 exe: " + $GameExe)
    }
    else {
        $selfTestFile = Join-Path $work 'selftest.json'
        $gameLog      = Join-Path $work 'game.log'

        $proc = Start-Process -FilePath $GameExe -PassThru -ArgumentList (
            '-logFile "{0}" -po.selftest-report="{1}"' -f $gameLog, $selfTestFile)

        $deadline = (Get-Date).AddSeconds($LaunchTimeoutSec)
        while ((Get-Date) -lt $deadline) {
            if (Test-Path $selfTestFile) { break }
            if ($proc.HasExited) { break }
            Start-Sleep -Milliseconds 500
        }

        $got = Test-Path $selfTestFile
        if (-not $proc.HasExited) {
            try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop } catch { }
        }

        # 保留游戏日志便于事后排查
        $gameLogCopy = Join-Path $artifacts ("game-" + $stamp + ".log")
        if (Test-Path $gameLog) { Copy-Item $gameLog $gameLogCopy -Force }

        if ($got) {
            try {
                $selfTest = Get-Content $selfTestFile -Raw | ConvertFrom-Json
                $healthy = [bool]$selfTest.healthy
                Add-Step 'launch' $healthy ("patchedMethods=" + $selfTest.patchedMethods +
                    " prefix=" + $selfTest.prefixes + " postfix=" + $selfTest.postfixes +
                    " transpiler=" + $selfTest.transpilers)
            }
            catch {
                Add-Step 'launch' $false ("自检报告解析失败: " + $_.Exception.Message)
            }
        }
        else {
            Add-Step 'launch' $false ("超时 " + $LaunchTimeoutSec + "s 未生成自检报告（加载可能崩溃）。日志: " + (Split-Path -Leaf $gameLogCopy))
        }
    }
}

# ---------------------------------------------------------------- 5. 报告
$failed  = @($steps | Where-Object { -not $_.ok })
$overall = ($failed.Count -eq 0)

$report = [ordered]@{
    stage     = $(if ($Launch) { 'L1' } else { 'L0' })
    startedAt = $stamp
    ok        = $overall
    steps     = @($steps)
    dll       = $modDll
    selfTest  = $selfTest
}
$reportPath = Join-Path $artifacts 'gate-report.json'
$report | ConvertTo-Json -Depth 6 | Set-Content -Path $reportPath -Encoding UTF8

Write-Host ""
Write-Host ("== 结果: " + $(if ($overall) { 'PASS' } else { 'FAIL' }) + " ==")
Write-Host "报告: $reportPath"

if ($overall) { exit 0 } else { exit 1 }
