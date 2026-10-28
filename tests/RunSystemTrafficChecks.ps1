$ErrorActionPreference = 'Stop'
$routeSystemRoot = Split-Path $PSScriptRoot -Parent
$routeSystemBuild = Join-Path $routeSystemRoot 'work\system-check-build'
New-Item -Path $routeSystemBuild -ItemType Directory -Force | Out-Null
$routeSystemExe = Join-Path $routeSystemBuild 'SystemTrafficChecks.exe'
$routeSystemCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $routeSystemCompiler /nologo /target:exe /codepage:65001 /utf8output ('/out:' + $routeSystemExe) /reference:System.dll /reference:System.Core.dll /reference:System.Management.dll (Join-Path $routeSystemRoot 'src\SystemTrafficMonitor.cs') (Join-Path $PSScriptRoot 'SystemTrafficChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'System traffic checks compile failed' }
& $routeSystemExe
if ($LASTEXITCODE -ne 0) { throw 'System traffic checks failed' }
