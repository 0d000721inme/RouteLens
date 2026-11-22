$ErrorActionPreference = 'Stop'
$mapRoot = Split-Path -Parent $PSScriptRoot
$mapBuild = Join-Path $PSScriptRoot '.build'
New-Item -ItemType Directory -Force -Path $mapBuild | Out-Null
$mapCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$mapOutput = Join-Path $mapBuild 'MapResizePerformanceChecks.exe'
$mapArgs = @('/nologo', '/target:exe', '/main:MapResizePerformanceChecks', '/codepage:65001', '/utf8output',
    ('/out:' + $mapOutput), '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll', '/reference:System.Management.dll',
    ('/resource:' + (Join-Path $mapRoot 'src\maps\world-map.json') + ',Maps.world-map.json'))
$mapArgs += @(Get-ChildItem -LiteralPath (Join-Path $mapRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$mapArgs += Join-Path $PSScriptRoot 'MapResizePerformanceChecks.cs'
& $mapCompiler $mapArgs
if ($LASTEXITCODE -ne 0) { throw 'Map resize performance checks failed to compile.' }
& $mapOutput (Join-Path $mapBuild 'world-map-resize-preview.png')
if ($LASTEXITCODE -ne 0) { throw 'Map resize performance checks failed.' }
