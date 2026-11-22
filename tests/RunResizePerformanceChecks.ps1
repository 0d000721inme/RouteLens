param([string]$AppPath)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
if (-not $AppPath) { $AppPath = Join-Path $project 'RouteLens.exe' }
$source = (Resolve-Path -LiteralPath $AppPath).Path
# Keep the fixture's local cache and history paths away from user data.
$stage = Join-Path (Join-Path $project 'work\resize-performance') ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$appCopy = Join-Path $stage 'RouteLens.exe'
Copy-Item -LiteralPath $source -Destination $appCopy
$fixture = Join-Path $stage 'ResizePerformanceChecks.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$compile = @('/nologo', '/target:exe', '/codepage:65001', ('/out:' + $fixture), ('/reference:' + $appCopy),
    ('/win32manifest:' + (Join-Path $project 'src\app.manifest')), '/reference:System.dll',
    '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
    (Join-Path $PSScriptRoot 'ResizePerformanceChecks.cs'))
& $compiler $compile
if ($LASTEXITCODE -ne 0) { throw 'Resize performance fixture compilation failed.' }
& $fixture
if ($LASTEXITCODE -ne 0) { throw 'Resize performance fixture failed.' }
