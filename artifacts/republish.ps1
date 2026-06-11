$ErrorActionPreference = 'Stop'
Set-Location 'I:\lpppp\envoy'
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
$dotnet = 'I:\SoftWare\dotnet\dotnet.exe'
$publishDir = 'artifacts\package\DevSwitch-win10-x64'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
$pp = Start-Process -FilePath $dotnet -WorkingDirectory 'I:\lpppp\envoy' -ArgumentList @('publish','src\DevSwitch.App\DevSwitch.App.csproj','--configuration','Release','-p:Platform=x64','--output',$publishDir) -NoNewWindow -Wait -PassThru
Write-Output ("PUBLISH=" + $pp.ExitCode)
