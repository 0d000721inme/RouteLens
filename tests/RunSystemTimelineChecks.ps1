$ErrorActionPreference = 'Stop'
$routeRoot = Split-Path -Parent $PSScriptRoot
$routeBuild = Join-Path $routeRoot 'work\timeline-tests'
New-Item -ItemType Directory -Force -Path $routeBuild | Out-Null
$routeArgs = @('/nologo', '/target:exe', '/codepage:65001', '/utf8output', ('/out:' + (Join-Path $routeBuild 'SystemTimelineChecks.exe')),
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
    (Join-Path $routeRoot 'src\UiKit.cs'), (Join-Path $routeRoot 'src\SystemTimeline.cs'), (Join-Path $routeRoot 'src\SystemTimelineDialog.cs'), (Join-Path $routeRoot 'src\StartupSnapshot.cs'), (Join-Path $routeRoot 'src\StartupSnapshotDialog.cs'),
    (Join-Path $PSScriptRoot 'SystemTimelineChecks.cs'))
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' $routeArgs
if ($LASTEXITCODE -ne 0) { throw 'System timeline check compilation failed.' }
& (Join-Path $routeBuild 'SystemTimelineChecks.exe')
if ($LASTEXITCODE -ne 0) { throw 'System timeline checks failed.' }
