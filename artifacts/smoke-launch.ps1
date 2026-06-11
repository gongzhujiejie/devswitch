$ErrorActionPreference = 'Continue'
$exe = 'I:\lpppp\envoy\artifacts\package\DevSwitch-win10-x64-princess\DevSwitch.App.exe'
if (-not (Test-Path $exe)) { Write-Output 'EXE_MISSING'; exit 1 }
$proc = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 8
$alive = -not $proc.HasExited
Write-Output ("ALIVE=" + $alive + " PID=" + $proc.Id)
if ($alive) {
    try { $proc.CloseMainWindow() | Out-Null } catch {}
    Start-Sleep -Seconds 2
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    Write-Output 'CLOSED'
} else {
    Write-Output ('EXITCODE=' + $proc.ExitCode)
}
