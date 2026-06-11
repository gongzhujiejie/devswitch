$ErrorActionPreference = 'Stop'
Set-Location 'I:\lpppp\envoy'
$publishDir = 'artifacts\package\DevSwitch-win10-x64'
$princessDir = 'artifacts\package\DevSwitch-win10-x64-princess'
if (Test-Path $princessDir) { Remove-Item $princessDir -Recurse -Force }
Copy-Item $publishDir $princessDir -Recurse
$releaseBin = 'I:\lpppp\envoy\src\DevSwitch.App\bin\x64\Release\net8.0-windows10.0.19041.0\win10-x64'
foreach ($asset in @('App.xbf','MainWindow.xbf','DevSwitch.App.pri','princess.ico')) {
    $source = Join-Path $releaseBin $asset
    $destination = Join-Path $princessDir $asset
    if (Test-Path $source) { Copy-Item $source $destination -Force; Write-Output "copied $asset" }
    else { Write-Warning "missing release asset: $asset" }
}
# 把 helper exe 一并打入包，让 UI 通过 BaseDirectory 直接找到（切换/验证/重置/诊断依赖它）。
$helperSrc = 'I:\lpppp\envoy\artifacts\bin\DevSwitch.Helper.exe'
if (Test-Path $helperSrc) { Copy-Item $helperSrc (Join-Path $princessDir 'DevSwitch.Helper.exe') -Force; Write-Output 'copied DevSwitch.Helper.exe' }
else { Write-Warning 'missing helper exe' }
$zipPath = 'artifacts\package\DevSwitch-win10-x64-princess.zip'
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $princessDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
$zipInfo = Get-Item $zipPath
Write-Output ("ZIP=" + $zipInfo.FullName + " SIZE=" + [math]::Round($zipInfo.Length / 1MB, 2) + "MB")
Write-Output 'PACKAGE_DONE'
