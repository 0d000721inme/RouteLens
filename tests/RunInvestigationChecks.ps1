$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $PSScriptRoot '.build'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$executable = Join-Path $output 'InvestigationChecks.exe'
& $compiler /nologo /target:exe /platform:anycpu /codepage:65001 /utf8output ('/out:' + $executable) /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll (Join-Path $root 'src\Models.cs') (Join-Path $root 'src\TrafficTracker.cs') (Join-Path $root 'src\InvestigationMonitor.cs') (Join-Path $PSScriptRoot 'InvestigationChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Investigation check compilation failed.' }
& $executable
if ($LASTEXITCODE -ne 0) { throw 'Investigation checks failed.' }
