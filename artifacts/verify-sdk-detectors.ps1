# 文件用途：验证 DevSwitch SDK 根目录识别并行合并后的全量测试。
# 创建日期：2026-06-09
# NOTE: 只修复当前进程环境，不写注册表。

$ErrorActionPreference = 'Stop'
Set-Location 'I:\lpppp\envoy'

$env:DOTNET_ROOT = 'I:\SoftWare\dotnet'
$env:LOCALAPPDATA = 'C:\Users\11714\AppData\Local'
$env:APPDATA = 'C:\Users\11714\AppData\Roaming'
$env:TEMP = 'C:\Users\11714\AppData\Local\Temp'
$env:TMP = 'C:\Users\11714\AppData\Local\Temp'
$env:NUGET_PACKAGES = 'C:\Users\11714\.nuget\packages'
$env:DOTNET_CLI_HOME = 'C:\Users\11714'
$env:Path = 'I:\SoftWare\dotnet;I:\SoftWare\dotnet\tools;C:\Windows\System32;C:\Windows;C:\Windows\System32\WindowsPowerShell\v1.0;C:\Users\11714\scoop\apps\mingw\current\bin;C:\Users\11714\scoop\shims;' + $env:Path

New-Item -ItemType Directory -Path 'artifacts\bin', $env:LOCALAPPDATA, $env:APPDATA, $env:TEMP, $env:NUGET_PACKAGES -Force | Out-Null

# NOTE: 确保 helper 产物存在，避免 helper xUnit 测试因缺产物失败。
& 'C:\Users\11714\scoop\apps\mingw\current\bin\g++.exe' -std=c++20 -O2 -Wall -Wextra -static -static-libgcc -static-libstdc++ -o 'artifacts\bin\DevSwitch.Helper.exe' 'src\DevSwitch.Helper\main.cpp'

$out = 'artifacts\verify-sdk-detectors.out.txt'
$err = 'artifacts\verify-sdk-detectors.err.txt'
Remove-Item $out, $err -ErrorAction SilentlyContinue
$p = Start-Process -FilePath 'I:\SoftWare\dotnet\dotnet.exe' -ArgumentList @('test', 'tests\DevSwitch.Tests\DevSwitch.Tests.csproj', '--configuration', 'Debug', '--verbosity', 'minimal', '--no-restore') -WorkingDirectory 'I:\lpppp\envoy' -NoNewWindow -Wait -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
Write-Output "EXIT=$($p.ExitCode)"
if (Test-Path $out) { Get-Content $out -Raw }
if (Test-Path $err) { Get-Content $err -Raw }
exit $p.ExitCode
