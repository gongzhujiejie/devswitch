# 文件用途：运行 DevSwitch M0 行为测试。
# 创建日期：2026-06-09
# 语言版本要求：Windows PowerShell 5.1+
# 依赖库/工具：已构建的 DevSwitch.Helper.exe；dotnet SDK 用于 xUnit 正式测试
# NOTE: 合法授权学习使用，仅限本地环境。

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$helperPath = Join-Path $repoRoot 'artifacts\bin\DevSwitch.Helper.exe'
$testProject = Join-Path $repoRoot 'tests\DevSwitch.Tests\DevSwitch.Tests.csproj'
$userProfile = [Environment]::GetFolderPath('UserProfile')
if ([string]::IsNullOrWhiteSpace($userProfile)) {
    $userProfile = [Environment]::GetEnvironmentVariable('USERPROFILE', 'Process')
}
if ([string]::IsNullOrWhiteSpace($userProfile)) {
    $userProfile = 'C:\Users\11714'
}

# NOTE: 当前自动化 Shell 可能缺少 Windows 用户目录环境变量，NuGet restore/test 会因此失败。
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

New-Item -ItemType Directory -Path $localAppData, $appData, $tempDir, $nugetPackages -Force | Out-Null

if (-not (Test-Path $helperPath)) {
    throw "helper executable was not found at $helperPath. Run scripts\build.ps1 first."
}

function Invoke-HelperJson {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Json
    )

    # NOTE: 通过真实进程 stdin/stdout 验证 helper 协议，不调用内部函数。
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo.FileName = $helperPath
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.RedirectStandardInput = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    $process.StartInfo.CreateNoWindow = $true
    [void]$process.Start()
    $process.StandardInput.WriteLine($Json)
    $process.StandardInput.Close()
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()

    return [PSCustomObject]@{
        ExitCode = $process.ExitCode
        Stdout = $stdout
        Stderr = $stderr
        Response = if ([string]::IsNullOrWhiteSpace($stdout)) { $null } else { $stdout | ConvertFrom-Json }
    }
}

$pingResult = Invoke-HelperJson '{"requestId":"smoke-test","operation":"ping","payload":{}}'
if ($pingResult.ExitCode -ne 0) {
    throw "helper ping smoke test failed with exit code $($pingResult.ExitCode). stderr: $($pingResult.Stderr)"
}
if ($pingResult.Response.requestId -ne 'smoke-test' -or $pingResult.Response.success -ne $true -or $pingResult.Response.errorCode -ne $null -or $pingResult.Response.message -ne 'pong') {
    throw "helper ping smoke test returned unexpected response: $($pingResult.Stdout)"
}
Write-Host 'Helper ping smoke test passed.'

$unknownResult = Invoke-HelperJson '{"requestId":"unknown-test","operation":"does-not-exist","payload":{}}'
if ($unknownResult.ExitCode -eq 0) {
    throw 'helper unknown operation smoke test should exit with a non-zero code.'
}
if ($unknownResult.Response.requestId -ne 'unknown-test' -or $unknownResult.Response.success -ne $false -or $unknownResult.Response.errorCode -ne 'unknown-operation') {
    throw "helper unknown operation smoke test returned unexpected response: $($unknownResult.Stdout)"
}
Write-Host 'Helper unknown operation smoke test passed.'

$invalidResult = Invoke-HelperJson '{"operation":"ping","payload":{}}'
if ($invalidResult.ExitCode -eq 0) {
    throw 'helper invalid request smoke test should exit with a non-zero code.'
}
if ($invalidResult.Response.success -ne $false -or $invalidResult.Response.errorCode -ne 'invalid-request' -or $invalidResult.Response.message -ne 'missing requestId') {
    throw "helper invalid request smoke test returned unexpected response: $($invalidResult.Stdout)"
}
Write-Host 'Helper invalid request smoke test passed.'

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet -and (Test-Path 'C:\Program Files\dotnet\dotnet.exe')) {
    $dotnet = Get-Item 'C:\Program Files\dotnet\dotnet.exe'
}

if ($dotnet) {
    & $dotnet.Source test $testProject --configuration Debug
}
else {
    Write-Warning 'dotnet SDK was not found; skipped xUnit tests.'
}
