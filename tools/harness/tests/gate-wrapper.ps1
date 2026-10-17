$ErrorActionPreference = 'Stop'
function global:Get-Process {
    $nameIndex = [Array]::IndexOf($args, '-Name')
    if ($nameIndex -ge 0 -and $nameIndex + 1 -lt $args.Count) {
        $names = @($args[$nameIndex + 1] -split ',')
        if ($names -contains 'RimWorldWin64') { return @() }
    }
    Microsoft.PowerShell.Management\Get-Process @args
}
function global:dotnet {
    if ($env:POQA_CASE -eq 'exception') { throw 'injected build exception' }
    $output = @($args | Where-Object { $_ -like '-p:OutputPath=*' })[0].Substring(14)
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    if ($args[1] -like '*PawnOwnership.csproj') {
        if ($env:POQA_CASE -eq 'build-fail') { $global:LASTEXITCODE = 1; return }
        [IO.File]::WriteAllText((Join-Path $output 'PawnOwnership.dll'), 'candidate')
    } else {
        if ($env:POQA_CASE -eq 'audit-build-fail') { $global:LASTEXITCODE = 1; return }
        Copy-Item $env:POQA_EXE (Join-Path $output 'PatchAudit.exe')
    }
    $global:LASTEXITCODE = 0
}
& $env:POQA_SCRIPT -Deploy -Launch -GameModPath $env:POQA_MOD -GameExe $env:POQA_EXE -LaunchTimeoutSec 3
exit $LASTEXITCODE
