#requires -Version 7.4
param([Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference='Stop'
$mabiRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$mabiDestination=[IO.Path]::GetFullPath($Destination)
if(Test-Path -LiteralPath $mabiDestination){throw 'Destination must be a new directory'}
$mabiFiles=@('.gitignore','.gitattributes','README.md','THIRD_PARTY_NOTICES.md','Directory.Build.props','package.json','package-lock.json','Config/乐器列表.txt',
    'docs/BUILD.md','docs/guide.zh-CN.txt','docs/CHANGELOG.md','docs/online-updates.md','adapters/README.md','adapters/tw-20261003-gc-map.json','adapters/tw-20261015-identity-schema.json',
    'tools/Build-Preview.ps1','tools/Build-Release.ps1','tools/Test-Quick.ps1','tools/Test-Preview.ps1','tools/Test-OnlineUpdate.ps1','tools/New-UpdateAssets.ps1','tools/New-PortableHost.ps1','tools/New-AppIcon.ps1','tools/Export-PublicSource.ps1',
    'src/MabiSync.App/Assets/MabiSync.ico','src/MabiSync.App/Assets/MabiSync.png','src/MabiSync.App/Assets/MabiSync.svg')
foreach($mabiFolder in @('src','server','tests')){
    $mabiFiles+=@(& rg --files (Join-Path $mabiRoot $mabiFolder) -g '!**/bin/**' -g '!**/obj/**' -g '!**/__pycache__/**' | Where-Object {
        $_ -match '\.(cs|csproj|xaml|manifest|js|mjs)$' -or $_ -match 'tw-(character-synthetic\.bin|music-synthetic\.json)$'
    } | ForEach-Object { [IO.Path]::GetRelativePath($mabiRoot,$_) })
}
foreach($mabiRelative in $mabiFiles){
    $mabiSource=Join-Path $mabiRoot $mabiRelative
    if(!(Test-Path -LiteralPath $mabiSource -PathType Leaf)){throw ('Missing export file: '+$mabiRelative)}
    $mabiTarget=Join-Path $mabiDestination $mabiRelative
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($mabiTarget)) -Force | Out-Null
    Copy-Item -LiteralPath $mabiSource -Destination $mabiTarget
}
Write-Output ('Exported '+$mabiFiles.Count+' source files to '+$mabiDestination)
