$ErrorActionPreference = 'Stop'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    throw '需要 Windows 自带的 .NET Framework 4.x 编译器。'
}
$sourceRoot = Join-Path $PSScriptRoot 'src'
$buildArgs = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/utf8output',
    '/codepage:65001', ('/out:' + (Join-Path $PSScriptRoot 'RouteLens.exe')),
    ('/win32manifest:' + (Join-Path $sourceRoot 'app.manifest')),
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll', '/reference:System.Management.dll')
Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'flags') -Filter '*.png' -ErrorAction SilentlyContinue | ForEach-Object {
    $buildArgs += '/resource:' + $_.FullName + ',Flags.' + $_.Name
}
Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'maps') -Filter '*.json' -ErrorAction SilentlyContinue | ForEach-Object {
    $buildArgs += '/resource:' + $_.FullName + ',Maps.' + $_.Name
}
Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' | ForEach-Object { $buildArgs += $_.FullName }
& $compilerPath $buildArgs
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
Write-Output ('已生成：' + (Join-Path $PSScriptRoot 'RouteLens.exe'))
