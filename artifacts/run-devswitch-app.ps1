# 文件用途：定位并启动 DevSwitch.App.exe，便于 WinUI 3 空壳手工测试。
# 创建日期：2026-06-09
# 语言版本要求：Windows PowerShell 5.1+ / PowerShell 7+
# 依赖库/工具：I:\SoftWare\dotnet、已成功构建的 DevSwitch.App.exe。
# NOTE: 合法授权学习使用，仅限本地环境。本脚本只修改当前 PowerShell 进程环境，不写注册表。

$ErrorActionPreference = 'Stop'

# NOTE: 固定使用仓库当前约定的 .NET SDK 根目录，避免误用系统其它 dotnet 安装。
$env:DOTNET_ROOT = 'I:\SoftWare\dotnet'
$dotnetTools = Join-Path $env:DOTNET_ROOT 'tools'
$env:Path = (@($env:DOTNET_ROOT, $dotnetTools) + (($env:Path -split ';') | Where-Object {
    $_ -and
    ($_ -ne $env:DOTNET_ROOT) -and
    ($_ -ne $dotnetTools)
})) -join ';'

# NOTE: 脚本放在 artifacts 下，因此仓库根目录是脚本目录的上一级。
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$appProjectRoot = Join-Path $repoRoot 'src\DevSwitch.App'

# NOTE: WinUI 3 产物路径会随配置、平台、TFM、打包方式变化；这里按常见 Debug 输出做候选定位。
$candidatePatterns = @(
    'src\DevSwitch.App\bin\Debug\**\DevSwitch.App.exe',
    'src\DevSwitch.App\bin\x64\Debug\**\DevSwitch.App.exe',
    'src\DevSwitch.App\bin\Debug\net*-windows*\**\DevSwitch.App.exe',
    'src\DevSwitch.App\bin\Release\**\DevSwitch.App.exe',
    'src\DevSwitch.App\bin\x64\Release\**\DevSwitch.App.exe'
)

# NOTE: 逐个模式搜索 exe，并优先选择最新修改的产物，减少误启动旧构建的概率。
$appExe = $candidatePatterns |
    ForEach-Object { Get-ChildItem -Path (Join-Path $repoRoot $_) -File -ErrorAction SilentlyContinue } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $appExe) {
    Write-Host '未找到 DevSwitch.App.exe。' -ForegroundColor Yellow
    Write-Host '请先修复/完成 WinUI 构建，并运行类似命令：' -ForegroundColor Yellow
    Write-Host '  I:\SoftWare\dotnet\dotnet.exe build .\DevSwitch.sln --configuration Debug' -ForegroundColor Cyan
    Write-Host '如果 App 尚未加入 solution，可先构建 src\DevSwitch.App\DevSwitch.App.csproj。' -ForegroundColor Cyan
    Write-Host '若构建仍阻塞，请先查看 dotnet build 输出中的首个错误。' -ForegroundColor Yellow
    exit 1
}

Write-Host "DOTNET_ROOT=$env:DOTNET_ROOT"
Write-Host "启动 DevSwitch.App：$($appExe.FullName)"

# NOTE: 使用 Start-Process 启动 GUI 程序，并将工作目录设为 App 项目目录，便于相对资源路径解析。
Start-Process -FilePath $appExe.FullName -WorkingDirectory $appProjectRoot
