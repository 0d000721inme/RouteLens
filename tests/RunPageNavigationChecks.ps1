$ErrorActionPreference = 'Stop'
$routeRoot = Split-Path -Parent $PSScriptRoot
$routeBuild = Join-Path $PSScriptRoot '.build'
New-Item -ItemType Directory -Force -Path $routeBuild | Out-Null
$routeCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$routeArgs = @('/nologo', '/target:exe', '/main:PageNavigationChecks', '/codepage:65001', '/utf8output', ('/out:' + (Join-Path $routeBuild 'PageNavigationChecks.exe')), '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll', '/reference:System.Management.dll')
$routeArgs += @(Get-ChildItem -LiteralPath (Join-Path $routeRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$routeArgs += Join-Path $PSScriptRoot 'PageNavigationChecks.cs'
& $routeCompiler $routeArgs
if ($LASTEXITCODE -ne 0) { throw 'Page navigation check compilation failed.' }
& (Join-Path $routeBuild 'PageNavigationChecks.exe')
if ($LASTEXITCODE -ne 0) { throw 'Page navigation checks failed.' }
