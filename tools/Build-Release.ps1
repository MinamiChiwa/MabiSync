#requires -Version 7.4
param([string]$NodePath,[string]$CloudflaredPath,[string]$NodeLicensePath,[string]$CloudflaredLicensePath,[switch]$SkipChecks)
$ErrorActionPreference='Stop'
[xml]$mabiProject=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../src/MabiSync.App/MabiSync.App.csproj')
$mabiVersion=[string]$mabiProject.Project.PropertyGroup.Version
if($mabiVersion -notmatch '^\d+\.\d+\.\d+$'){throw 'A stable semantic version is required'}
& "$PSScriptRoot/Build-Preview.ps1" -NodePath $NodePath -CloudflaredPath $CloudflaredPath -NodeLicensePath $NodeLicensePath -CloudflaredLicensePath $CloudflaredLicensePath -Label $mabiVersion -Release -SkipChecks:$SkipChecks
