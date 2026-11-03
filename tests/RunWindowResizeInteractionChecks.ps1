param([switch]$Interactive)
$ErrorActionPreference = 'Stop'
$routeRoot = Split-Path -Parent $PSScriptRoot
$routeBuild = Join-Path $PSScriptRoot '.build'
New-Item -ItemType Directory -Force -Path $routeBuild | Out-Null
$routeCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$routeOutput = Join-Path $routeBuild 'WindowResizeInteractionChecks.exe'
$routeArgs = @('/nologo', '/target:winexe', '/main:WindowResizeInteractionChecks', '/codepage:65001', '/utf8output', ('/win32manifest:' + (Join-Path $routeRoot 'src\app.manifest')), ('/out:' + $routeOutput), '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll', '/reference:System.Management.dll')
$routeArgs += @(Get-ChildItem -LiteralPath (Join-Path $routeRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$routeArgs += Join-Path $PSScriptRoot 'WindowResizeInteractionChecks.cs'
& $routeCompiler $routeArgs
if ($LASTEXITCODE -ne 0) { throw 'Window resize interaction fixture compilation failed.' }
Write-Output ('Built interaction fixture: ' + $routeOutput)
Write-Output 'No drag has been performed by this script. Launch the fixture, drag the actual window edges, and inspect Bounds plus native message counters in its title. Ctrl+R resets the counters.'
if ($Interactive) { & $routeOutput }
