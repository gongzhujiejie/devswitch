# 文件用途：在 Visual Studio Developer PowerShell 环境中构建 DevSwitch.App。
# 创建日期：2026-06-09
# NOTE: 只修复当前进程环境，不写注册表。

$ErrorActionPreference = 'Stop'
Set-Location 'I:\lpppp\envoy'

Import-Module 'I:\SoftWare\Microsoft Visual Studio\2022\Community\Common7\Tools\Microsoft.VisualStudio.DevShell.dll'
Enter-VsDevShell -VsInstallPath 'I:\SoftWare\Microsoft Visual Studio\2022\Community' -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64'

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
$env:Path = 'I:\SoftWare\dotnet;I:\SoftWare\dotnet\tools;' + $env:Path

$out = 'artifacts\app-vsdevshell.out.txt'
$err = 'artifacts\app-vsdevshell.err.txt'
Remove-Item $out, $err -ErrorAction SilentlyContinue
$p = Start-Process -FilePath 'msbuild.exe' -ArgumentList @('src\DevSwitch.App\DevSwitch.App.csproj', '/t:Restore;Build', '/p:Configuration=Debug', '/p:Platform=x64', '/v:minimal') -WorkingDirectory 'I:\lpppp\envoy' -NoNewWindow -Wait -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
Write-Output "EXIT=$($p.ExitCode)"
if (Test-Path $out) { Get-Content $out -Raw }
if (Test-Path $err) { Get-Content $err -Raw }
exit $p.ExitCode
