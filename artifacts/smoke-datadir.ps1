$ErrorActionPreference = 'Continue'
$pkgDir = 'I:\lpppp\envoy\artifacts\package\DevSwitch-win10-x64-princess'
$exe = Join-Path $pkgDir 'DevSwitch.App.exe'
$dataDir = Join-Path $pkgDir 'data'
# 清掉可能存在的旧 data，确保验证的是本次启动新建。
if (Test-Path $dataDir) { Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue }
$proc = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 9
$alive = -not $proc.HasExited
Write-Output ("ALIVE=" + $alive)
if ($alive) {
    try { $proc.CloseMainWindow() | Out-Null } catch {}
    Start-Sleep -Seconds 2
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}
# 验证数据目录是否落在应用同目录 data\
if (Test-Path $dataDir) {
    Write-Output "DATA_IN_APP_DIR=True"
    Get-ChildItem $dataDir -Recurse -ErrorAction SilentlyContinue | Select-Object -First 10 -ExpandProperty FullName
} else {
    Write-Output "DATA_IN_APP_DIR=False"
}
