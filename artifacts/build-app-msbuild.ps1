# 文件用途：使用 Visual Studio MSBuild 构建 DevSwitch.App WinUI 空壳。
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
$env:DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR = 'I:\SoftWare\dotnet\sdk\11.0.100-preview.4.26230.115\Sdks'
$env:DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER = '11.0.100-preview.4.26230.115'
$env:MSBuildSDKsPath = 'I:\SoftWare\dotnet\sdk\11.0.100-preview.4.26230.115\Sdks'
$env:ProgramFiles = 'C:\Program Files'
$env:ProgramData = 'C:\ProgramData'
$env:ALLUSERSPROFILE = 'C:\ProgramData'
$env:SystemDrive = 'C:'
$env:PUBLIC = 'C:\Users\Public'
$env:Path = 'I:\SoftWare\dotnet;I:\SoftWare\dotnet\tools;C:\Windows\System32;C:\Windows;C:\Windows\System32\WindowsPowerShell\v1.0;C:\Users\11714\scoop\shims;' + $env:Path

$out = 'artifacts\app-msbuild.out.txt'
$err = 'artifacts\app-msbuild.err.txt'
Remove-Item $out, $err -ErrorAction SilentlyContinue
$p = Start-Process -FilePath 'I:\SoftWare\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe' -ArgumentList @('src\DevSwitch.App\DevSwitch.App.csproj', '/t:Restore;Build', '/p:Configuration=Debug', '/p:Platform=x64', '/v:minimal') -WorkingDirectory 'I:\lpppp\envoy' -NoNewWindow -Wait -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
Write-Output "EXIT=$($p.ExitCode)"
if (Test-Path $out) { Get-Content $out -Raw }
if (Test-Path $err) { Get-Content $err -Raw }
exit $p.ExitCode
