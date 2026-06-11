# 文件用途：将 DevSwitch.App 加入 solution 并尝试构建 WinUI 3 手工测试空壳。
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
$env:ProgramFiles = 'C:\Program Files'
$env:ProgramData = 'C:\ProgramData'
$env:ALLUSERSPROFILE = 'C:\ProgramData'
$env:SystemDrive = 'C:'
$env:PUBLIC = 'C:\Users\Public'
$env:Path = 'I:\SoftWare\dotnet;I:\SoftWare\dotnet\tools;C:\Windows\System32;C:\Windows;C:\Windows\System32\WindowsPowerShell\v1.0;C:\Users\11714\scoop\shims;' + $env:Path

New-Item -ItemType Directory -Path $env:LOCALAPPDATA, $env:APPDATA, $env:TEMP, $env:NUGET_PACKAGES, $env:ProgramData, $env:PUBLIC -Force | Out-Null

& 'I:\SoftWare\dotnet\dotnet.exe' sln 'DevSwitch.sln' add 'src\DevSwitch.App\DevSwitch.App.csproj'

$out = 'artifacts\winui-build.out.txt'
$err = 'artifacts\winui-build.err.txt'
Remove-Item $out, $err -ErrorAction SilentlyContinue
$p = Start-Process -FilePath 'I:\SoftWare\dotnet\dotnet.exe' -ArgumentList @('build', 'src\DevSwitch.App\DevSwitch.App.csproj', '--configuration', 'Debug', '--verbosity', 'minimal') -WorkingDirectory 'I:\lpppp\envoy' -NoNewWindow -Wait -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
Write-Output "EXIT=$($p.ExitCode)"
if (Test-Path $out) { Get-Content $out -Raw }
if (Test-Path $err) { Get-Content $err -Raw }
exit $p.ExitCode
