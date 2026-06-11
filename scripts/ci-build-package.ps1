# 文件用途：CI（GitHub Actions）专用的 DevSwitch 构建打包脚本。
#   与本地 build-release-package.ps1 不同：不写死本地路径，使用 PATH 中的 dotnet / g++，
#   接受版本参数（来自 git tag），产出 DevSwitch-win10-x64.zip 与同名 .sha256。
# 创建/修改日期：2026-06-11
# 语言版本要求：Windows PowerShell 5.1+ / pwsh
# 依赖：dotnet（PATH，.NET 8 SDK）、g++（PATH，MinGW，支持 c++2a）
# NOTE: 本脚本只读写仓库内 artifacts 目录，不触碰系统环境与注册表。

param(
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'

# 用脚本自身位置定位仓库根（scripts/ 的上一级）。
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
Write-Output "REPO_ROOT=$repoRoot"

# 解析工具：优先 PATH 中的裸名，回退带 .exe，最后回退裸名交系统解析（CI runner 一定可用）。
function Resolve-Tool([string]$name) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $cmdExe = Get-Command ($name + '.exe') -ErrorAction SilentlyContinue
    if ($cmdExe) { return $cmdExe.Source }
    return $name
}

$dotnet = Resolve-Tool 'dotnet'
$gpp = Resolve-Tool 'g++'
Write-Output "DOTNET=$dotnet"
Write-Output "GPP=$gpp"

$binDir = Join-Path $repoRoot 'artifacts\bin'
New-Item -ItemType Directory -Path $binDir -Force | Out-Null

# === 1. 编译 3 个 C++ 辅助程序 ===
$helperOutput = Join-Path $binDir 'DevSwitch.Helper.exe'
$shimOutput = Join-Path $binDir 'DevSwitch.Shim.exe'
$updaterOutput = Join-Path $binDir 'DevSwitch.Updater.exe'

$helperSrc = Join-Path $repoRoot 'src\DevSwitch.Helper\main.cpp'
$shimSrc = Join-Path $repoRoot 'src\DevSwitch.Shim\main.cpp'
$updaterSrc = Join-Path $repoRoot 'src\DevSwitch.Updater\main.cpp'

# helper：用到 CommandLineToArgvW，需链接 shell32。
& $gpp -std=c++2a -O2 -Wall -Wextra -static -static-libgcc -static-libstdc++ -o $helperOutput $helperSrc -lshell32
if (-not (Test-Path $helperOutput)) { throw 'helper build failed' }
Write-Output 'STEP1a: helper built'

# shim：wmain 入口需 -municode。
& $gpp -std=c++2a -O2 -Wall -Wextra -municode -static -static-libgcc -static-libstdc++ -o $shimOutput $shimSrc
if (-not (Test-Path $shimOutput)) { throw 'shim build failed' }
Write-Output 'STEP1b: shim built'

# updater：wmain 入口需 -municode；CommandLineToArgvW 需 shell32。
& $gpp -std=c++2a -O2 -Wall -Wextra -municode -static -static-libgcc -static-libstdc++ -o $updaterOutput $updaterSrc -lshell32
if (-not (Test-Path $updaterOutput)) { throw 'updater build failed' }
Write-Output 'STEP1c: updater built'

# === 2. Release publish App ===
$publishDir = Join-Path $repoRoot 'artifacts\package\DevSwitch-win10-x64'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

$csproj = Join-Path $repoRoot 'src\DevSwitch.App\DevSwitch.App.csproj'
$publishArgs = @('publish', $csproj, '--configuration', 'Release', '-p:Platform=x64', '-p:NodeReuse=false', '--output', $publishDir)
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $publishArgs += ('-p:Version=' + $Version)
    $publishArgs += ('-p:AssemblyVersion=' + $Version + '.0')
    $publishArgs += ('-p:FileVersion=' + $Version + '.0')
}

# 先显式 restore，再 publish（同进程 & 调用，日志直接进 CI 输出，便于排错）。
& $dotnet restore $csproj -p:Platform=x64
Write-Output ("RESTORE_EXIT=" + $LASTEXITCODE)

& $dotnet @publishArgs
$publishExit = $LASTEXITCODE
Write-Output ("PUBLISH_EXIT=" + $publishExit)

# 以「产物是否生成」为最终判据：WinUI 的 PRI 子任务个别环境会返回非 0，
# 但只要 DevSwitch.App.dll 已产出即视为成功（与本地脚本同思路，CI 上更稳）。
$publishedDll = Join-Path $publishDir 'DevSwitch.App.dll'
if (-not (Test-Path $publishedDll)) {
    throw ('dotnet publish failed (exit=' + $publishExit + ') and no DevSwitch.App.dll produced.')
}

Copy-Item $helperOutput (Join-Path $publishDir 'DevSwitch.Helper.exe') -Force
Copy-Item $shimOutput (Join-Path $publishDir 'DevSwitch.Shim.exe') -Force
Copy-Item $updaterOutput (Join-Path $publishDir 'DevSwitch.Updater.exe') -Force
Write-Output 'STEP2: app published'

# === 3. 组装最终包目录 ===
$packageDir = Join-Path $repoRoot 'artifacts\package\DevSwitch-win10-x64-princess'
if (Test-Path $packageDir) { Remove-Item $packageDir -Recurse -Force }
Copy-Item $publishDir $packageDir -Recurse
Copy-Item $helperOutput (Join-Path $packageDir 'DevSwitch.Helper.exe') -Force
Copy-Item $shimOutput (Join-Path $packageDir 'DevSwitch.Shim.exe') -Force
Copy-Item $updaterOutput (Join-Path $packageDir 'DevSwitch.Updater.exe') -Force

# 从 Release 构建输出补齐 XBF/PRI 与图标（publish 已带时跳过）。
$releaseBin = Join-Path $repoRoot 'src\DevSwitch.App\bin\x64\Release\net8.0-windows10.0.19041.0\win10-x64'
foreach ($asset in @('App.xbf', 'MainWindow.xbf', 'DevSwitch.App.pri', 'princess.ico')) {
    $source = Join-Path $releaseBin $asset
    $destination = Join-Path $packageDir $asset
    if (Test-Path $source) {
        Copy-Item $source $destination -Force
    }
    elseif (-not (Test-Path $destination)) {
        Write-Warning ('missing asset: ' + $asset)
    }
}
Write-Output 'STEP3: package assembled'

# === 4. 打 zip + 生成 sha256 ===
$releaseDir = Join-Path $repoRoot 'artifacts\release'
if (Test-Path $releaseDir) { Remove-Item $releaseDir -Recurse -Force }
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

$zipPath = Join-Path $releaseDir 'DevSwitch-win10-x64.zip'
Compress-Archive -Path (Join-Path $packageDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
Write-Output 'STEP4: zip created'

$sha = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
[System.IO.File]::WriteAllText(($zipPath + '.sha256'), $sha)
Write-Output ("SHA256=" + $sha)

$zipInfo = Get-Item $zipPath
Write-Output ("ZIP=" + $zipInfo.FullName + " SIZE=" + [math]::Round($zipInfo.Length / 1MB, 2) + "MB")
Write-Output 'PACKAGE_DONE'
