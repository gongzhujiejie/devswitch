$ErrorActionPreference = 'Continue'
$exe = 'I:\lpppp\envoy\src\DevSwitch.App\bin\x64\Release\net8.0-windows10.0.19041.0\win10-x64\DevSwitch.App.exe'
if (-not (Test-Path $exe)) { Write-Output 'EXE_MISSING'; exit 1 }
$proc = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 9
$alive = -not $proc.HasExited
Write-Output ("ALIVE=" + $alive + " PID=" + $proc.Id)
if ($alive) {
    try { $proc.CloseMainWindow() | Out-Null } catch {}
    Start-Sleep -Seconds 2
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    Write-Output 'CLOSED_OK'
} else {
    Write-Output ('EXITCODE=' + $proc.ExitCode)
}
