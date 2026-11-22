$ErrorActionPreference = 'Stop'
$routeRoot = Split-Path -Parent $PSScriptRoot
$routeBuild = Join-Path $PSScriptRoot '.build'
New-Item -ItemType Directory -Force -Path $routeBuild | Out-Null
$routeCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$routeArgs = @('/nologo', '/target:exe', '/main:ThemeChecks', '/codepage:65001', '/utf8output',
    ('/out:' + (Join-Path $routeBuild 'ThemeChecks.exe')), '/reference:System.dll',
    '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
    '/reference:System.Web.Extensions.dll', '/reference:System.Management.dll')
$routeArgs += @(Get-ChildItem -LiteralPath (Join-Path $routeRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$routeArgs += Join-Path $PSScriptRoot 'ThemeChecks.cs'
& $routeCompiler $routeArgs
if ($LASTEXITCODE -ne 0) { throw 'Theme check compilation failed.' }
& (Join-Path $routeBuild 'ThemeChecks.exe')
if ($LASTEXITCODE -ne 0) { throw 'Theme checks failed.' }
