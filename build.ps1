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
$countryDatabase = Join-Path $PSScriptRoot 'resources\dbip-country-lite.csv.gz'
if (-not (Test-Path -LiteralPath $countryDatabase)) { throw '缺少内置 DB-IP 国家数据库：resources\dbip-country-lite.csv.gz。' }
$buildArgs += '/resource:' + $countryDatabase + ',Geo.dbip-country-lite.csv.gz'
Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' | ForEach-Object { $buildArgs += $_.FullName }
$iconPath = Join-Path ([System.IO.Path]::GetTempPath()) ('RouteLensIcon-' + [guid]::NewGuid().ToString('N') + '.ico')
$iconGeneratorPath = [System.IO.Path]::ChangeExtension($iconPath, '.exe')
try {
    $iconGeneratorArgs = @('/nologo', '/target:exe', '/define:GENERATE_ICON',
        ('/out:' + $iconGeneratorPath), '/reference:System.dll', '/reference:System.Drawing.dll',
        (Join-Path $sourceRoot 'BrandIcon.cs'))
    & $compilerPath $iconGeneratorArgs
    if ($LASTEXITCODE -ne 0) { throw '图标生成器编译失败。' }
    & $iconGeneratorPath $iconPath
    if ($LASTEXITCODE -ne 0) { throw '图标生成失败。' }
    $buildArgs += '/win32icon:' + $iconPath
    & $compilerPath $buildArgs
    if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
}
finally {
    Remove-Item -LiteralPath $iconGeneratorPath, $iconPath -Force -ErrorAction SilentlyContinue
}
Write-Output ('已生成：' + (Join-Path $PSScriptRoot 'RouteLens.exe'))
