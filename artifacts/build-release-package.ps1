# 文件用途：构建并打包 DevSwitch Release 版本（含 helper、publish App、princess 资源与 zip）。
# 创建/修改日期：2026-06-09
# 语言版本要求：Windows PowerShell 5.1+
# 依赖库/工具：I:\SoftWare\dotnet、MinGW g++（c++2a）
# NOTE: 合法授权学习使用，仅限本地环境。本脚本只修复当前进程环境，不写注册表。

$ErrorActionPreference = 'Stop'
Set-Location 'I:\lpppp\envoy'

# NOTE: 自动化 Shell 缺少 Windows 用户目录环境变量，dotnet restore/publish 依赖它们。
$env:DOTNET_ROOT = 'I:\SoftWare\dotnet'
$env:USERPROFILE = 'C:\Users\11714'
$env:LOCALAPPDATA = 'C:\Users\11714\AppData\Local'
$env:APPDATA = 'C:\Users\11714\AppData\Roaming'
$env:TEMP = 'C:\Users\11714\AppData\Local\Temp'
$env:TMP = 'C:\Users\11714\AppData\Local\Temp'
$env:NUGET_PACKAGES = 'C:\Users\11714\.nuget\packages'
$env:DOTNET_CLI_HOME = 'C:\Users\11714'
$env:ProgramFiles = 'C:\Program Files'
$env:ProgramData = 'C:\ProgramData'
$env:SystemDrive = 'C:'
$env:PUBLIC = 'C:\Users\Public'
$env:Path = 'I:\SoftWare\dotnet;I:\SoftWare\dotnet\tools;C:\Windows\System32;C:\Windows;C:\Windows\System32\WindowsPowerShell\v1.0;C:\Users\11714\scoop\apps\mingw\current\bin;C:\Users\11714\scoop\shims;' + $env:Path

$dotnet = 'I:\SoftWare\dotnet\dotnet.exe'
$gpp = 'C:\Users\11714\scoop\apps\mingw\current\bin\g++.exe'

# === 1. 构建 C++ helper 与 shim（旧 MinGW 仅支持 c++2a） ===
New-Item -ItemType Directory -Path 'artifacts\bin' -Force | Out-Null
$helperOutput = 'artifacts\bin\DevSwitch.Helper.exe'
# helper 用到 CommandLineToArgvW（提权 CLI 模式），需链接 shell32。
& $gpp -std=c++2a -O2 -Wall -Wextra -static -static-libgcc -static-libstdc++ -o $helperOutput 'src\DevSwitch.Helper\main.cpp' -lshell32
if (-not (Test-Path $helperOutput)) { throw 'helper build failed' }
Write-Output 'STEP1a: helper built'

# shim 转发器：被复制成 shims\<cmd>.exe，由系统 PATH 命中后转发到 current\<type>\bin 下真实可执行。
# 需要 -municode（wmain 入口）；纯 Win32，无额外依赖。
$shimOutput = 'artifacts\bin\DevSwitch.Shim.exe'
& $gpp -std=c++2a -O2 -Wall -Wextra -municode -static -static-libgcc -static-libstdc++ -o $shimOutput 'src\DevSwitch.Shim\main.cpp'
if (-not (Test-Path $shimOutput)) { throw 'shim build failed' }
Write-Output 'STEP1b: shim built'

# updater 自更新器：主程序退出后覆盖安装目录并重启。需要 -municode（wmain）与 -lshell32（CommandLineToArgvW）。
$updaterOutput = 'artifacts\bin\DevSwitch.Updater.exe'
& $gpp -std=c++2a -O2 -Wall -Wextra -municode -static -static-libgcc -static-libstdc++ -o $updaterOutput 'src\DevSwitch.Updater\main.cpp' -lshell32
if (-not (Test-Path $updaterOutput)) { throw 'updater build failed' }
Write-Output 'STEP1c: updater built'

# === 2. Release publish App ===
$publishDir = 'artifacts\package\DevSwitch-win10-x64'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
$publishProc = Start-Process -FilePath $dotnet -WorkingDirectory 'I:\lpppp\envoy' -ArgumentList @('publish', 'src\DevSwitch.App\DevSwitch.App.csproj', '--configuration', 'Release', '-p:Platform=x64', '-p:NodeReuse=false', '--output', $publishDir) -NoNewWindow -Wait -PassThru
if ($publishProc.ExitCode -ne 0) { throw "dotnet publish failed: $($publishProc.ExitCode)" }
# NOTE: 保险起见显式补拷 helper 与 shim。csproj 也声明了发布项，但脚本层兜底可避免复制条件/增量构建导致漏带。
Copy-Item $helperOutput (Join-Path $publishDir 'DevSwitch.Helper.exe') -Force
Copy-Item $shimOutput (Join-Path $publishDir 'DevSwitch.Shim.exe') -Force
Copy-Item $updaterOutput (Join-Path $publishDir 'DevSwitch.Updater.exe') -Force
Write-Output 'STEP2: app published'

# === 3. 组装 princess 包 ===
$princessDir = 'artifacts\package\DevSwitch-win10-x64-princess'
if (Test-Path $princessDir) { Remove-Item $princessDir -Recurse -Force }
Copy-Item $publishDir $princessDir -Recurse
Copy-Item $helperOutput (Join-Path $princessDir 'DevSwitch.Helper.exe') -Force
Copy-Item $shimOutput (Join-Path $princessDir 'DevSwitch.Shim.exe') -Force
Copy-Item $updaterOutput (Join-Path $princessDir 'DevSwitch.Updater.exe') -Force

# NOTE: publish 不会带 XBF/PRI 编译产物与 princess 图标，从 Release 构建输出补齐，保证窗口图标与 XAML 资源正常。
$releaseBin = Join-Path 'I:\lpppp\envoy' 'src\DevSwitch.App\bin\x64\Release\net8.0-windows10.0.19041.0\win10-x64'
foreach ($asset in @('App.xbf', 'MainWindow.xbf', 'DevSwitch.App.pri', 'princess.ico')) {
    $source = Join-Path $releaseBin $asset
    $destination = Join-Path $princessDir $asset
    if (Test-Path $source) {
        Copy-Item $source $destination -Force
    }
    else {
        Write-Warning "missing release asset: $asset"
    }
}
Write-Output 'STEP3: princess package assembled'

# === 4. 重新打 zip ===
$zipPath = 'artifacts\package\DevSwitch-win10-x64-princess.zip'
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $princessDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
Write-Output 'STEP4: zip created'

$zipInfo = Get-Item $zipPath
Write-Output ("ZIP=" + $zipInfo.FullName + " SIZE=" + [math]::Round($zipInfo.Length / 1MB, 2) + "MB")
Write-Output 'PACKAGE_DONE'
