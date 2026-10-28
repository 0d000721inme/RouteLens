$ErrorActionPreference = 'Stop'
$routeOfflineRoot = Split-Path $PSScriptRoot -Parent
$routeOfflineSource = Join-Path $routeOfflineRoot 'src'
$routeOfflineBuild = Join-Path $PSScriptRoot '.build'
New-Item -ItemType Directory -Path $routeOfflineBuild -Force | Out-Null
$routeOfflineExe = Join-Path $routeOfflineBuild 'OfflineCountryChecks.exe'
$routeOfflineCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$routeOfflineArgs = @('/nologo','/target:exe','/utf8output','/codepage:65001',('/out:' + $routeOfflineExe),'/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll')
foreach ($routeOfflineFile in @('Models.cs','ConnectionReader.cs','Flags.cs','GeoService.cs','OfflineCountryDatabase.cs')) { $routeOfflineArgs += Join-Path $routeOfflineSource $routeOfflineFile }
$routeOfflineArgs += Join-Path $PSScriptRoot 'OfflineCountryChecks.cs'
& $routeOfflineCompiler $routeOfflineArgs
if ($LASTEXITCODE -ne 0) { throw 'Offline database test compilation failed.' }
& $routeOfflineExe
if ($LASTEXITCODE -ne 0) { throw 'Offline database test failed.' }
