$ErrorActionPreference = 'Stop'
$routeAlertRoot = Split-Path $PSScriptRoot -Parent
$routeAlertBuild = Join-Path $PSScriptRoot '.build'
New-Item -ItemType Directory -Path $routeAlertBuild -Force | Out-Null
$routeAlertOutput = Join-Path $routeAlertBuild 'ActivityAlertsChecks.exe'
$routeAlertCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$routeAlertSources = @('Models.cs','TrafficTracker.cs','ActivityAlerts.cs') | ForEach-Object { Join-Path $routeAlertRoot ('src\' + $_) }
& $routeAlertCompiler /nologo /target:exe /codepage:65001 /utf8output ('/out:' + $routeAlertOutput) /reference:System.dll /reference:System.Core.dll $routeAlertSources (Join-Path $PSScriptRoot 'ActivityAlertsChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Alert checks failed to compile' }
& $routeAlertOutput
if ($LASTEXITCODE -ne 0) { throw 'Alert checks failed' }
