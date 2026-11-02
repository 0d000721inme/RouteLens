$ErrorActionPreference = 'Stop'
$routeRoot = Split-Path -Parent $PSScriptRoot
$routeBuild = Join-Path $PSScriptRoot '.build'
New-Item -ItemType Directory -Force -Path $routeBuild | Out-Null
$routeCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$routeArgs = @('/nologo', '/target:exe', '/codepage:65001', '/utf8output', ('/out:' + (Join-Path $routeBuild 'PageTransitionChecks.exe')), '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', (Join-Path $routeRoot 'src\UiKit.cs'), (Join-Path $routeRoot 'src\PageTransition.cs'), (Join-Path $PSScriptRoot 'PageTransitionChecks.cs'))
& $routeCompiler $routeArgs
if ($LASTEXITCODE -ne 0) { throw 'Transition check compilation failed.' }
& (Join-Path $routeBuild 'PageTransitionChecks.exe')
if ($LASTEXITCODE -ne 0) { throw 'Transition checks failed.' }
