$ErrorActionPreference = 'Stop'
$routeHistoryRoot = Split-Path $PSScriptRoot -Parent
$routeHistoryBuild = Join-Path $routeHistoryRoot 'work\history-check-build'
New-Item -Path $routeHistoryBuild -ItemType Directory -Force | Out-Null
$routeHistoryExe = Join-Path $routeHistoryBuild 'HistoryChecks.exe'
$routeHistoryCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $routeHistoryCompiler /nologo /target:exe /codepage:65001 /utf8output ('/out:' + $routeHistoryExe) /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll (Join-Path $routeHistoryRoot 'src\Models.cs') (Join-Path $routeHistoryRoot 'src\HistoryStore.cs') (Join-Path $PSScriptRoot 'HistoryChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'History checks compile failed' }
& $routeHistoryExe
if ($LASTEXITCODE -ne 0) { throw 'History checks failed' }
