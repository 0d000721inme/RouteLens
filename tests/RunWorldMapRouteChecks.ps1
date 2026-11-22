param([string]$ScreenshotPath)
$ErrorActionPreference = 'Stop'
$routeRoot = Split-Path -Parent $PSScriptRoot
$routeBuild = Join-Path $PSScriptRoot '.build'
New-Item -ItemType Directory -Force -Path $routeBuild | Out-Null
$routeCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$routeExe = Join-Path $routeBuild 'WorldMapRouteChecks.exe'
$routeArgs = @('/nologo', '/target:exe', '/main:WorldMapRouteChecks', '/codepage:65001', '/utf8output', ('/out:' + $routeExe),
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
    '/reference:System.Web.Extensions.dll', '/reference:System.Management.dll', ('/resource:' + (Join-Path $routeRoot 'src\maps\world-map.json') + ',Maps.world-map.json'))
$routeArgs += @(Get-ChildItem -LiteralPath (Join-Path $routeRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$routeArgs += Join-Path $PSScriptRoot 'WorldMapRouteChecks.cs'
& $routeCompiler $routeArgs
if ($LASTEXITCODE -ne 0) { throw 'Route map check compilation failed.' }
$routeCheckArgs = @()
if ($ScreenshotPath) { $routeCheckArgs += $ScreenshotPath }
& $routeExe @routeCheckArgs
if ($LASTEXITCODE -ne 0) { throw 'Route map checks failed.' }
