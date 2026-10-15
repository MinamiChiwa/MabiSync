#requires -Version 7.4
param([string]$NodePath,[string]$CloudflaredPath,[switch]$SkipChecks)
$ErrorActionPreference='Stop'
[xml]$mabiProject=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../src/MabiSync.App/MabiSync.App.csproj')
$mabiVersion=[string]$mabiProject.Project.PropertyGroup.Version
if($mabiVersion -notmatch '^\d+\.\d+\.\d+$'){throw 'A stable semantic version is required'}
& "$PSScriptRoot/Build-Preview.ps1" -NodePath $NodePath -CloudflaredPath $CloudflaredPath -Label $mabiVersion -Release -SkipChecks:$SkipChecks
