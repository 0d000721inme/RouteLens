$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw '需要 Windows 的 .NET Framework 4.x 编译器。'
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$trackerSource = Join-Path $projectRoot 'src\TrafficTracker.cs'
$modelSource = Join-Path $projectRoot 'src\Models.cs'
$testBuildRoot = Join-Path $PSScriptRoot '.build'
New-Item -ItemType Directory -Path $testBuildRoot -Force | Out-Null
$testOutput = Join-Path $testBuildRoot 'TrafficTrackerChecks.exe'
& $compiler /nologo /target:exe /platform:anycpu /utf8output /codepage:65001 ('/out:' + $testOutput) /reference:System.dll /reference:System.Core.dll $modelSource $trackerSource (Join-Path $PSScriptRoot 'TrafficTrackerChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Traffic tracker checks failed to compile.' }
& $testOutput
if ($LASTEXITCODE -ne 0) { throw 'Traffic tracker checks failed.' }
