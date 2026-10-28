$ErrorActionPreference = 'Stop'
$routePrivacyRoot = Split-Path $PSScriptRoot -Parent
$routePrivacyBuild = Join-Path $PSScriptRoot '.build'
New-Item -ItemType Directory -Path $routePrivacyBuild -Force | Out-Null
$routePrivacyOutput = Join-Path $routePrivacyBuild 'PrivacyDnsChecks.exe'
$routePrivacyCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$routePrivacySources = @('UiKit.cs','PrivacyDnsTester.cs','PrivacyDnsDialog.cs') | ForEach-Object { Join-Path $routePrivacyRoot ('src\' + $_) }
& $routePrivacyCompiler /nologo /target:exe /codepage:65001 /utf8output ('/out:' + $routePrivacyOutput) /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll $routePrivacySources (Join-Path $PSScriptRoot 'PrivacyDnsChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Privacy checks failed to compile' }
& $routePrivacyOutput
if ($LASTEXITCODE -ne 0) { throw 'Privacy checks failed' }
