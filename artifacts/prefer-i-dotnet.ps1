# 文件用途：让 PowerShell 会话优先使用 I:\SoftWare\dotnet，而不是 C:\Program Files\dotnet。
# 创建日期：2026-06-09
# 语言版本要求：Windows PowerShell 5.1+ / PowerShell 7+
# 依赖库：无。

$ErrorActionPreference = 'Stop'

$dotnetRoot = 'I:\SoftWare\dotnet'
if (-not (Test-Path (Join-Path $dotnetRoot 'dotnet.exe'))) {
    throw "dotnet.exe was not found under $dotnetRoot"
}

$profileTargets = @(
    (Join-Path $env:USERPROFILE 'Documents\WindowsPowerShell\profile.ps1'),
    (Join-Path $env:USERPROFILE 'Documents\PowerShell\profile.ps1')
)

$block = @'
# DevSwitch/.NET SDK location preference
$devSwitchDotnetRoot = 'I:\SoftWare\dotnet'
if (Test-Path (Join-Path $devSwitchDotnetRoot 'dotnet.exe')) {
    $env:DOTNET_ROOT = $devSwitchDotnetRoot
    $dotnetTools = Join-Path $devSwitchDotnetRoot 'tools'
    $env:Path = (@($devSwitchDotnetRoot, $dotnetTools) + (($env:Path -split ';') | Where-Object {
        $_ -and
        ($_ -ne $devSwitchDotnetRoot) -and
        ($_ -ne $dotnetTools) -and
        ($_ -ne 'C:\Program Files\dotnet')
    })) -join ';'
}
'@

foreach ($profilePath in $profileTargets) {
    $profileDir = Split-Path -Parent $profilePath
    New-Item -ItemType Directory -Path $profileDir -Force | Out-Null

    if (Test-Path $profilePath) {
        $content = Get-Content $profilePath -Raw
    }
    else {
        $content = ''
    }

    if ($content -notlike '*DevSwitch/.NET SDK location preference*') {
        Add-Content -Path $profilePath -Value "`r`n$block"
        Write-Output "Updated profile: $profilePath"
    }
    else {
        Write-Output "Profile already contains DevSwitch dotnet preference: $profilePath"
    }
}

# 同时修复当前进程，便于立即验证。
$env:DOTNET_ROOT = $dotnetRoot
$dotnetToolsNow = Join-Path $dotnetRoot 'tools'
$env:Path = (@($dotnetRoot, $dotnetToolsNow) + (($env:Path -split ';') | Where-Object {
    $_ -and
    ($_ -ne $dotnetRoot) -and
    ($_ -ne $dotnetToolsNow) -and
    ($_ -ne 'C:\Program Files\dotnet')
})) -join ';'

Write-Output "Current DOTNET_ROOT=$env:DOTNET_ROOT"
Write-Output 'Current dotnet resolution:'
& where.exe dotnet
Write-Output 'SDKs from selected dotnet:'
& dotnet --list-sdks
