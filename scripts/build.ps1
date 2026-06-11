# 文件用途：构建 DevSwitch M0 工程骨架，包括 C# 项目和 C++ helper 最小可执行文件。
# 创建日期：2026-06-09
# 语言版本要求：Windows PowerShell 5.1+
# 依赖库/工具：dotnet SDK、MinGW g++ 或后续 MSVC cl
# NOTE: 合法授权学习使用，仅限本地环境。

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactDir = Join-Path $repoRoot 'artifacts\bin'
$helperSource = Join-Path $repoRoot 'src\DevSwitch.Helper\main.cpp'
$helperOutput = Join-Path $artifactDir 'DevSwitch.Helper.exe'
$userProfile = [Environment]::GetFolderPath('UserProfile')
if ([string]::IsNullOrWhiteSpace($userProfile)) {
    $userProfile = [Environment]::GetEnvironmentVariable('USERPROFILE', 'Process')
}
if ([string]::IsNullOrWhiteSpace($userProfile)) {
    $userProfile = 'C:\Users\11714'
}

# NOTE: 当前自动化 Shell 可能缺少 Windows 用户目录环境变量，NuGet restore 会因此失败。
# 这里仅修复当前进程环境，不写注册表，不改变用户系统配置。
$localAppData = Join-Path $userProfile 'AppData\Local'
$appData = Join-Path $userProfile 'AppData\Roaming'
$tempDir = Join-Path $localAppData 'Temp'
$nugetPackages = Join-Path $userProfile '.nuget\packages'

$env:LOCALAPPDATA = $localAppData
$env:APPDATA = $appData
$env:TEMP = $tempDir
$env:TMP = $tempDir
$env:NUGET_PACKAGES = $nugetPackages
$env:DOTNET_CLI_HOME = $userProfile
$env:Path = "C:\Windows\System32;C:\Windows;C:\Windows\System32\WindowsPowerShell\v1.0;$userProfile\scoop\apps\mingw\current\bin;$userProfile\scoop\shims;C:\Program Files\dotnet;$env:Path"

New-Item -ItemType Directory -Path $artifactDir, $localAppData, $appData, $tempDir, $nugetPackages -Force | Out-Null

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet -and (Test-Path 'C:\Program Files\dotnet\dotnet.exe')) {
    $dotnet = Get-Item 'C:\Program Files\dotnet\dotnet.exe'
}

if ($dotnet) {
    & $dotnet.Source build (Join-Path $repoRoot 'DevSwitch.sln') --configuration Debug
}
else {
    Write-Warning 'dotnet SDK was not found; skipped C# build.'
}

$gpp = Get-Command g++ -ErrorAction SilentlyContinue
if (-not $gpp) {
    $fallbackGpp = Join-Path $userProfile 'scoop\apps\mingw\current\bin\g++.exe'
    if (Test-Path $fallbackGpp) {
        $gpp = Get-Item $fallbackGpp
    }
    else {
        throw 'g++ was not found. Install mingw with Scoop or build the helper with MSVC in a later milestone.'
    }
}

# NOTE: 静态链接 MinGW 运行时，确保测试启动 helper 时不依赖额外 DLL 所在 PATH。
$compileArgs = "-std=c++20 -O2 -Wall -Wextra -static -static-libgcc -static-libstdc++ -o `"$helperOutput`" `"$helperSource`""
$compileProcess = Start-Process -FilePath $gpp.Source -ArgumentList $compileArgs -NoNewWindow -PassThru -Wait
if ($compileProcess.ExitCode -ne 0) {
    throw "helper build failed with exit code $($compileProcess.ExitCode)"
}

Write-Host "Built helper: $helperOutput"
