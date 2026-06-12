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

# Git Bash / 精简 shell 启动 powershell.exe 时可能缺少 ProgramFiles 系列环境变量，
# NuGet/MSBuild targets 会依赖这些变量拼装路径；缺失时会报 Value cannot be null(path1)。
if (-not $env:ProgramFiles) { $env:ProgramFiles = 'C:\Program Files' }
if (-not [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')) { Set-Item -Path 'Env:ProgramFiles(x86)' -Value 'C:\Program Files (x86)' }
if (-not $env:SystemRoot) { $env:SystemRoot = 'C:\Windows' }
if (-not $env:windir) { $env:windir = $env:SystemRoot }
if (-not $env:USERPROFILE) { $env:USERPROFILE = [Environment]::GetFolderPath('UserProfile') }
if (-not $env:LOCALAPPDATA) { $env:LOCALAPPDATA = [Environment]::GetFolderPath('LocalApplicationData') }
if (-not $env:APPDATA) { $env:APPDATA = [Environment]::GetFolderPath('ApplicationData') }
if (-not $env:TEMP) { $env:TEMP = [IO.Path]::GetTempPath().TrimEnd([IO.Path]::DirectorySeparatorChar) }
if (-not $env:TMP) { $env:TMP = $env:TEMP }

# 用脚本自身位置定位仓库根（scripts/ 的上一级）。
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
Write-Output "REPO_ROOT=$repoRoot"

# 解析工具：优先 PATH 中的裸名，回退带 .exe，最后回退裸名交系统解析（CI runner 一定可用）。
function Resolve-Tool {
    param([string]$name)

    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $cmdExe = Get-Command ($name + '.exe') -ErrorAction SilentlyContinue
    if ($cmdExe) { return $cmdExe.Source }
    return $name
}

function Join-ProcessArguments {
    param([string[]]$Arguments)

    return ($Arguments | ForEach-Object {
        if ($_ -match '[\s"]') {
            '"' + ($_ -replace '"', '\"') + '"'
        }
        else {
            $_
        }
    }) -join ' '
}

function Invoke-NativeProcess {
    param(
        [string]$FilePath,
        [string[]]$Arguments
    )

    $argumentLine = Join-ProcessArguments $Arguments
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $startInfo.Arguments = $argumentLine
    $startInfo.UseShellExecute = $false

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    [void]$process.Start()
    $process.WaitForExit()
    return $process.ExitCode
}

$dotnet = Resolve-Tool 'dotnet'

function Invoke-Dotnet {
    param([string[]]$Arguments)

    if ($env:GITHUB_ACTIONS -eq 'true') {
        & $dotnet @Arguments
        if ($null -eq $LASTEXITCODE) { return 0 }
        return $LASTEXITCODE
    }

    return Invoke-NativeProcess -FilePath $dotnet -Arguments $Arguments
}

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
$publishArgs = @(
    'publish', $csproj,
    '--configuration', 'Release',
    # ReadyToRun 必须绑定具体 RID；publish 复用显式 restore 的 assets，避免隐式 restore 丢参数。
    '--runtime', 'win10-x64',
    '--no-restore',
    '-p:Platform=x64',
    '-p:UseRidGraph=true',
    '-p:NodeReuse=false',
    # 发布包启用 ReadyToRun，降低用户首次启动时的 JIT 成本；包体略增但换取更快冷启动。
    '-p:PublishReadyToRun=true',
    '--output', $publishDir
)
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $publishArgs += ('-p:Version=' + $Version)
    $publishArgs += ('-p:AssemblyVersion=' + $Version + '.0')
    $publishArgs += ('-p:FileVersion=' + $Version + '.0')
}

# Windows App SDK 的 MrtCore.PriGen.targets 会从
#   $(MSBuildExtensionsPath)\Microsoft\VisualStudio\v$(VisualStudioVersion)\AppxPackage\Microsoft.Build.Packaging.Pri.Tasks.dll
# 加载 PRI 生成任务。用 `dotnet publish` 时该路径指向 .NET SDK 目录，而这个 DLL 只随 Visual Studio
# 的 MSIX/Appx 打包工具提供，SDK 目录里没有，于是报 MSB4062。
# 解决：用 vswhere 找到 runner 上的 VS 安装，定位其中真实存在该 DLL 的 AppxPackage 目录，
# 通过 -p:AppxMSBuildToolsPath 覆盖，让 UsingTask 能加载到正确程序集。
function Resolve-AppxToolsPath {
    $dllName = 'Microsoft.Build.Packaging.Pri.Tasks.dll'
    $programFiles = if ($env:ProgramFiles) { $env:ProgramFiles } else { 'C:\Program Files' }
    $programFilesX86Env = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    $programFilesX86 = if ($programFilesX86Env) { $programFilesX86Env } else { 'C:\Program Files (x86)' }

    # 1) 优先用 vswhere 定位 VS 安装根，在其中递归找真实存在该 DLL 的 AppxPackage 目录。
    $vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $vsRoots = & $vswhere -latest -products * -property installationPath 2>$null
        foreach ($vsRoot in @($vsRoots)) {
            if ([string]::IsNullOrWhiteSpace($vsRoot)) { continue }
            $found = Get-ChildItem -Path $vsRoot -Recurse -Filter $dllName -ErrorAction SilentlyContinue |
                Where-Object { $_.DirectoryName -match 'AppxPackage' } |
                Select-Object -First 1
            if ($found) { return $found.DirectoryName }
        }
    }

    # 2) 退回：扫描常见安装根下的 VS MSBuild AppxPackage 目录。
    $candidateRoots = @(
        (Join-Path $programFiles 'Microsoft Visual Studio'),
        (Join-Path $programFilesX86 'Microsoft Visual Studio'),
        'I:\SoftWare\Microsoft Visual Studio'
    )
    $roots = $candidateRoots | Where-Object { $_ -and (Test-Path $_) }
    foreach ($root in $roots) {
        $found = Get-ChildItem -Path $root -Recurse -Filter $dllName -ErrorAction SilentlyContinue |
            Where-Object { $_.DirectoryName -match 'AppxPackage' } |
            Select-Object -First 1
        if ($found) { return $found.DirectoryName }
    }

    return $null
}

$appxToolsPath = Resolve-AppxToolsPath
if ($appxToolsPath) {
    # 末尾保留反斜杠：targets 中 $(AppxMSBuildToolsPath)Microsoft.Build.AppxPackage.dll 未额外加分隔符。
    $normalizedAppxToolsPath = $appxToolsPath.TrimEnd([IO.Path]::DirectorySeparatorChar).Replace('\\', '/') + '/'
    $publishArgs += ('-p:AppxMSBuildToolsPath=' + $normalizedAppxToolsPath)
    Write-Output ("APPX_TOOLS=" + $appxToolsPath)
}
else {
    Write-Warning 'AppxMSBuildToolsPath not resolved via vswhere; PRI task may fail to load.'
}

# 先显式 restore，再 publish（同进程 & 调用，日志直接进 CI 输出，便于排错）。
# NOTE: ReadyToRun 编译器包在 restore 时解析；这里必须带 RID 与 PublishReadyToRun，publish 才能 --no-restore 稳定复用。
$restoreArgs = @(
    'restore', $csproj,
    '-p:Platform=x64',
    '-p:UseRidGraph=true',
    '-r', 'win10-x64',
    '-p:PublishReadyToRun=true'
)
$restoreExit = Invoke-Dotnet -Arguments $restoreArgs
Write-Output ("RESTORE_EXIT=" + $restoreExit)
if ($restoreExit -ne 0) {
    throw ('dotnet restore failed (exit=' + $restoreExit + ').')
}

$publishExit = Invoke-Dotnet -Arguments $publishArgs
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
