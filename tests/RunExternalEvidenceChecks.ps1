$ErrorActionPreference = 'Stop'
$routeRoot = Split-Path $PSScriptRoot -Parent
$routeBuild = Join-Path $PSScriptRoot '.build'
New-Item -ItemType Directory -Path $routeBuild -Force | Out-Null
$routeExe = Join-Path $routeBuild 'ExternalEvidenceChecks.exe'
$routeCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $routeCompiler /nologo /target:exe /utf8output /codepage:65001 ('/out:' + $routeExe) /reference:System.dll /reference:System.Web.Extensions.dll (Join-Path $routeRoot 'src\ExternalEvidence.cs') (Join-Path $PSScriptRoot 'ExternalEvidenceChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'External evidence test compilation failed.' }
& $routeExe
if ($LASTEXITCODE -ne 0) { throw 'External evidence checks failed.' }
