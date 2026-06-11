$ErrorActionPreference = 'Stop'
Set-Location 'I:\lpppp\envoy'
$princessDir = 'artifacts\package\DevSwitch-win10-x64-princess'
$helperSrc = 'I:\lpppp\envoy\artifacts\bin\DevSwitch.Helper.exe'
if (Test-Path $helperSrc) { Copy-Item $helperSrc (Join-Path $princessDir 'DevSwitch.Helper.exe') -Force; Write-Output 'copied helper' } else { Write-Warning 'missing helper exe' }
$zipPath = 'artifacts\package\DevSwitch-win10-x64-princess.zip'
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $princessDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
$zipInfo = Get-Item $zipPath
Write-Output ("ZIP=" + $zipInfo.FullName + " SIZE=" + [math]::Round($zipInfo.Length / 1MB, 2) + "MB")
Write-Output 'PACKAGE_DONE'
