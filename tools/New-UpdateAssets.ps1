#requires -Version 7.4
param(
    [Parameter(Mandatory=$true)][string]$Directory,
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][string]$PortableZip,
    [string[]]$BaselineDirectory=@()
)
$ErrorActionPreference='Stop'
$mabiRoot=(Resolve-Path -LiteralPath $Directory).Path
if($Version -notmatch '^\d+\.\d+\.\d+$'){throw 'Online updates target stable releases only'}
$mabiZip=(Resolve-Path -LiteralPath $PortableZip).Path
$mabiOutput=Join-Path (Split-Path -Parent $mabiZip) ('MabiSync-'+$Version+'-update-assets')
if(Test-Path -LiteralPath $mabiOutput){throw 'Update assets directory already exists'}
New-Item -ItemType Directory -Path $mabiOutput | Out-Null
$mabiFiles=@(Get-ChildItem -LiteralPath $mabiRoot -File -Recurse | Where-Object {
    $mabiRelative=[IO.Path]::GetRelativePath($mabiRoot,$_.FullName).Replace('\','/')
    $mabiRelative.StartsWith('App/') -or $mabiRelative.StartsWith('Docs/')
} | Sort-Object FullName | ForEach-Object {
    @{path=[IO.Path]::GetRelativePath($mabiRoot,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
if(!($mabiFiles.path -contains 'App/bootstrap-version.txt') -or !($mabiFiles.path -contains 'App/MabiSync.exe')){throw 'Target lacks online-update bootstrap support'}
$mabiPackages=[Collections.Generic.List[object]]::new()
$mabiPackages.Add(@{fromVersion=$null;assetName=[IO.Path]::GetFileName($mabiZip);archivePrefix=[IO.Path]::GetFileName($mabiRoot)+'/';paths=@($mabiFiles.path);bytes=(Get-Item -LiteralPath $mabiZip).Length;sha256=(Get-FileHash -LiteralPath $mabiZip -Algorithm SHA256).Hash})
$mabiCreated=[Collections.Generic.List[string]]::new()
$mabiSeen=[Collections.Generic.HashSet[string]]::new()
foreach($mabiBase in $BaselineDirectory){
    $mabiBaseRoot=(Resolve-Path -LiteralPath $mabiBase).Path
    $mabiBaseManifest=Get-Content -LiteralPath (Join-Path $mabiBaseRoot 'App/package-manifest.json') -Raw | ConvertFrom-Json
    $mabiBaseVersion=$mabiBaseManifest.label
    if($mabiBaseVersion -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$' -or !$mabiSeen.Add($mabiBaseVersion)){throw 'Baseline label is invalid or duplicated'}
    if(Test-Path -LiteralPath (Join-Path $mabiBaseRoot 'App/bootstrap-version.txt')){
        if((Get-FileHash -LiteralPath (Join-Path $mabiBaseRoot 'MabiSync.exe') -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $mabiRoot 'MabiSync.exe') -Algorithm SHA256).Hash){throw 'Bootstrap changed: increase its protocol version and require a complete portable upgrade'}
    }
    $mabiChanged=@($mabiFiles | Where-Object {
        $mabiPrevious=Join-Path $mabiBaseRoot $_.path
        !(Test-Path -LiteralPath $mabiPrevious -PathType Leaf) -or (Get-FileHash -LiteralPath $mabiPrevious -Algorithm SHA256).Hash -ne $_.sha256
    })
    $mabiDelta=Join-Path $mabiOutput ('MabiSync-'+$Version+'-from-'+$mabiBaseVersion+'-update.zip')
    if(Test-Path -LiteralPath $mabiDelta){throw 'Update archive output already exists'}
    $mabiArchive=[IO.Compression.ZipFile]::Open($mabiDelta,[IO.Compression.ZipArchiveMode]::Create)
    try {foreach($mabiFile in $mabiChanged){[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($mabiArchive,(Join-Path $mabiRoot $mabiFile.path),$mabiFile.path,[IO.Compression.CompressionLevel]::SmallestSize) | Out-Null}}
    finally{$mabiArchive.Dispose()}
    $mabiPackages.Add(@{fromVersion=$mabiBaseVersion;assetName=[IO.Path]::GetFileName($mabiDelta);archivePrefix='';paths=@($mabiChanged.path);bytes=(Get-Item -LiteralPath $mabiDelta).Length;sha256=(Get-FileHash -LiteralPath $mabiDelta -Algorithm SHA256).Hash})
    $mabiCreated.Add($mabiDelta)
}
$mabiManifest=Join-Path $mabiOutput 'MabiSync-update.json'
if(Test-Path -LiteralPath $mabiManifest){throw 'Update descriptor output already exists'}
@{schema=1;product='MabiSync';version=$Version;bootstrapVersion=1;files=$mabiFiles;packages=@($mabiPackages)} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $mabiManifest -Encoding utf8NoBOM
$mabiCreated.Add($mabiManifest)
foreach($mabiAsset in $mabiCreated){((Get-FileHash -LiteralPath $mabiAsset -Algorithm SHA256).Hash+'  '+[IO.Path]::GetFileName($mabiAsset)) | Set-Content -LiteralPath ($mabiAsset+'.sha256') -Encoding ascii}
$mabiCreated
