$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent
$root = Join-Path $repo ('artifacts/qa-gate-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $root | Out-Null
$exe = Join-Path $root 'ProtocolFixture.exe'
$source = @'
using System;
using System.IO;
class ProtocolFixture {
 static int Main(string[] args) {
  string mode=Environment.GetEnvironmentVariable("POQA_CASE");
  string reportArg=null;
  foreach(string arg in args) if(arg.StartsWith("-po.selftest-report=")) reportArg=arg.Substring(20).Trim('\u0022');
  if(reportArg==null) {
   if(mode=="audit-fail") { Console.WriteLine("=== RESULT ok=30 fail=1 ==="); return 1; }
   if(mode=="audit-short") { Console.WriteLine("=== RESULT ok=0 fail=0 ==="); return 0; }
   Console.WriteLine("=== RESULT ok=31 fail=0 ==="); return 0;
  }
  File.WriteAllText(Environment.GetEnvironmentVariable("POQA_MARKER"),"launched");
  File.WriteAllText(reportArg,"{\"healthy\":true,\"patchedMethods\":31,\"prefixes\":20,\"postfixes\":11,\"transpilers\":0}");
  return 0;
 }
}
'@
if (-not (Test-Path $exe)) { Add-Type -TypeDefinition $source -OutputAssembly $exe -OutputType ConsoleApplication }
$env:POQA_EXE = $exe
$results = @()
foreach ($case in @('build-fail','audit-build-fail','audit-fail','audit-short','deploy-fail','exception','valid')) {
    $dir = Join-Path $root $case
    $scriptDir = Join-Path $dir 'tools\harness'
    New-Item -ItemType Directory -Force -Path $scriptDir | Out-Null
    Copy-Item (Join-Path $repo 'tools\harness\run-tests.ps1') (Join-Path $scriptDir 'run-tests.ps1')
    $env:POQA_CASE = $case
    $env:POQA_SCRIPT = Join-Path $scriptDir 'run-tests.ps1'
    $env:POQA_MOD = Join-Path $dir 'mod'
    $env:POQA_MARKER = Join-Path $dir 'game-launched.txt'
    $target = Join-Path $env:POQA_MOD '1.6\Assemblies'
    if ($case -ne 'deploy-fail') {
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        [IO.File]::WriteAllText((Join-Path $target 'PawnOwnership.dll'), 'original')
    }
    $out = Join-Path $dir 'stdout.log'
    $err = Join-Path $dir 'stderr.log'
    $wrapper = Join-Path $PSScriptRoot 'gate-wrapper.ps1'
    $proc = Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"{0}"' -f $wrapper)) -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
    $report = Get-Content (Join-Path $dir 'artifacts\gate-report.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $launched = Test-Path $env:POQA_MARKER
    if ($case -eq 'valid') {
        if ($proc.ExitCode -ne 0 -or -not $report.ok -or -not $launched) { throw "Valid fixture failed: $out" }
    } else {
        if ($proc.ExitCode -ne 1 -or $report.ok -or $launched) { throw "Failure not contained: $case $out" }
        if ($case -ne 'deploy-fail' -and (Get-Content (Join-Path $target 'PawnOwnership.dll') -Raw) -ne 'original') { throw "Failed gate deployed: $case" }
    }
    $results += [ordered]@{case=$case; exit=$proc.ExitCode; ok=$report.ok; launched=$launched; pass=$true}
    Write-Host "PASS $case exit=$($proc.ExitCode) launched=$launched"
}
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $root 'results.json') -Encoding UTF8
Write-Host 'RESULT 7/7'
