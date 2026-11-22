$ErrorActionPreference = 'Stop'
$usageRoot = Split-Path $PSScriptRoot -Parent
$usageBuild = Join-Path $PSScriptRoot '.build'
$usageImages = Join-Path $usageRoot 'work\usage-strip-checks'
New-Item -ItemType Directory -Force -Path $usageBuild, $usageImages | Out-Null
$usageCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$usageTest = Join-Path $usageBuild 'UsageStripChecks.exe'
$usageSources = @('Models.cs','TrafficTracker.cs','UiKit.cs','UsageStrip.cs') | ForEach-Object { Join-Path $usageRoot ('src\' + $_) }
& $usageCompiler /nologo /target:exe /codepage:65001 /utf8output ('/out:' + $usageTest) /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll $usageSources (Join-Path $PSScriptRoot 'UsageStripChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Usage strip checks failed to compile' }
& $usageTest $usageImages
if ($LASTEXITCODE -ne 0) { throw 'Usage strip checks failed' }
