$ErrorActionPreference = 'Stop'
$traceRoot = Split-Path -Parent $PSScriptRoot
$traceBuild = Join-Path $traceRoot 'work\connection-trace-tests'
New-Item -ItemType Directory -Force -Path $traceBuild | Out-Null
$traceExe = Join-Path $traceBuild 'ConnectionTraceChecks.exe'
$traceArgs = @('/nologo', '/target:exe', '/codepage:65001', '/utf8output', ('/out:' + $traceExe),
    '/reference:System.dll', '/reference:System.Core.dll', (Join-Path $traceRoot 'src\ConnectionTrace.cs'),
    (Join-Path $PSScriptRoot 'ConnectionTraceChecks.cs'))
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') $traceArgs
if ($LASTEXITCODE -ne 0) { throw 'Connection trace check compilation failed.' }
& $traceExe
if ($LASTEXITCODE -ne 0) { throw 'Connection trace checks failed.' }
