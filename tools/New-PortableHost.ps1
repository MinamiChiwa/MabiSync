#requires -Version 7.4
param([Parameter(Mandatory=$true)][string]$ApplicationDirectory, [Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
$mabiAppDirectory = (Resolve-Path -LiteralPath $ApplicationDirectory).Path
$mabiDotnetRoot = Split-Path -Parent (Get-Command dotnet -ErrorAction Stop).Source
$mabiSdkVersion = (& dotnet --version).Trim()
$mabiHostModel = Join-Path $mabiDotnetRoot ('sdk/'+$mabiSdkVersion+'/Microsoft.NET.HostModel.dll')
Add-Type -Path $mabiHostModel
$mabiHostPack = Get-ChildItem -LiteralPath (Join-Path $mabiDotnetRoot 'packs/Microsoft.NETCore.App.Host.win-x64') -Directory |
    Where-Object { $_.Name -match '^8\.0\.\d+$' } | Sort-Object { [Version]$_.Name } -Descending | Select-Object -First 1
if (!$mabiHostPack) { throw 'The .NET 8 Windows x64 apphost pack is required' }
$mabiHostTemplate = Join-Path $mabiHostPack.FullName 'runtimes/win-x64/native/apphost.exe'
# Use the SDK host writer, preserving application resources and the native GUI
# entry point while locating both the managed application and runtime in App/.
[Microsoft.NET.HostModel.AppHost.HostWriter]::CreateAppHost($mabiHostTemplate, [IO.Path]::GetFullPath($OutputPath), 'App/MabiSync.dll', $true, (Join-Path $mabiAppDirectory 'MabiSync.dll'), $false)
