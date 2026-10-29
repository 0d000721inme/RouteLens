$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'tests\.build'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /codepage:65001 /utf8output ('/out:' + (Join-Path $output 'EvidenceChecks.exe')) (Join-Path $root 'src\Models.cs') (Join-Path $root 'src\EvidenceMonitor.cs') (Join-Path $root 'tests\EvidenceMonitorChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Evidence check compilation failed.' }
& (Join-Path $output 'EvidenceChecks.exe')
if ($LASTEXITCODE -ne 0) { throw 'Evidence checks failed.' }
