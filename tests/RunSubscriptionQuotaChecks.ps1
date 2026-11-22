param([switch]$Local)
$ErrorActionPreference = 'Stop'
$quotaRoot = Split-Path -Parent $PSScriptRoot
$quotaBuild = Join-Path $quotaRoot 'work\subscription-quota-tests'
New-Item -ItemType Directory -Force -Path $quotaBuild | Out-Null
$quotaExe = Join-Path $quotaBuild 'SubscriptionQuotaChecks.exe'
$quotaArgs = @('/nologo', '/target:exe', '/codepage:65001', '/utf8output', ('/out:' + $quotaExe),
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Web.Extensions.dll',
    (Join-Path $quotaRoot 'src\Models.cs'), (Join-Path $quotaRoot 'src\ClashConnectionReader.cs'),
    (Join-Path $quotaRoot 'src\ConnectionReader.cs'), (Join-Path $quotaRoot 'src\SubscriptionQuotaReader.cs'),
    (Join-Path $PSScriptRoot 'SubscriptionQuotaChecks.cs'))
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') $quotaArgs
if ($LASTEXITCODE -ne 0) { throw 'Subscription quota check compilation failed.' }
if ($Local) { & $quotaExe '--local' } else { & $quotaExe }
if ($LASTEXITCODE -ne 0) { throw 'Subscription quota checks failed.' }
