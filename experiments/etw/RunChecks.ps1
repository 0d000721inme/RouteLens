$ErrorActionPreference = 'Stop'
$routeOutput = Join-Path ([System.IO.Path]::GetTempPath()) ('RouteLens-EtwChecks-' + [Guid]::NewGuid().ToString('N') + '.exe')
try {
    $routeCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $routeCompiler /nologo /codepage:65001 /utf8output /target:exe ('/out:' + $routeOutput) /reference:System.dll (Join-Path $PSScriptRoot 'NetworkEventDecoder.cs') (Join-Path $PSScriptRoot 'EtwExperiment.cs') (Join-Path $PSScriptRoot 'EtwExperimentChecks.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
    & $routeOutput
    if ($LASTEXITCODE -ne 0) { throw 'Checks failed.' }
} finally { if (Test-Path -LiteralPath $routeOutput) { Remove-Item -LiteralPath $routeOutput } }
