param([switch]$Interactive)
$ErrorActionPreference = 'Stop'
$routeRoot = Split-Path -Parent $PSScriptRoot
$routeBuild = Join-Path $PSScriptRoot '.build'
New-Item -ItemType Directory -Force -Path $routeBuild | Out-Null
$routeCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$routeArgs = @('/nologo', '/target:exe', '/main:WindowChromeChecks', '/codepage:65001', '/utf8output', ('/win32manifest:' + (Join-Path $routeRoot 'src\app.manifest')), ('/out:' + (Join-Path $routeBuild 'WindowChromeChecks.exe')), '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll', '/reference:System.Management.dll')
$routeArgs += @(Get-ChildItem -LiteralPath (Join-Path $routeRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$routeArgs += Join-Path $PSScriptRoot 'WindowChromeChecks.cs'
& $routeCompiler $routeArgs
if ($LASTEXITCODE -ne 0) { throw 'Window chrome check compilation failed.' }
Copy-Item -LiteralPath (Join-Path $routeBuild 'WindowChromeChecks.exe') -Destination (Join-Path $routeBuild 'WindowChromeInteractive.exe') -Force
if ($Interactive) { & (Join-Path $routeBuild 'WindowChromeInteractive.exe') }
else { & (Join-Path $routeBuild 'WindowChromeChecks.exe') }
if ($LASTEXITCODE -ne 0) { throw 'Window chrome checks failed.' }
