$items = Get-ChildItem 'I:\SoftWare\Microsoft Visual Studio\2022\Community' -Recurse -Filter 'Microsoft.Build.Packaging.Pri.Tasks.dll' -ErrorAction SilentlyContinue
foreach ($item in $items) {
    Write-Output $item.FullName
}
