#requires -Version 7.4
param()
$ErrorActionPreference='Stop'
$mabiTestRoot=Join-Path ([IO.Path]::GetTempPath()) ('mabisync-bootstrap-check-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $mabiTestRoot | Out-Null
$mabiCompiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$mabiFixtureSource=Join-Path $mabiTestRoot 'fixture.cs'
@'
using System;
using System.IO;
class Fixture {
  static int Main(string[] args) {
    var root=Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).FullName;
    var version=File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"MabiSync.dll"));
    if(args.Length==2 && args[0]=="--check-package") {
      if(version=="broken")return 1;
      File.WriteAllText(args[1],"{\"started\":true,\"version\":\""+version+"\"}");return 0;
    }
    File.WriteAllText(Path.Combine(root,"launched.txt"),version);return 0;
  }
}
'@ | Set-Content -LiteralPath $mabiFixtureSource -Encoding utf8
$mabiFixtureExe=Join-Path $mabiTestRoot 'fixture.exe'
& $mabiCompiler /nologo /target:winexe /platform:x64 "/out:$mabiFixtureExe" $mabiFixtureSource
if($LASTEXITCODE -ne 0){throw 'Fixture compilation failed'}
& "$PSScriptRoot/New-PortableHost.ps1" -ApplicationDirectory $mabiTestRoot -OutputPath (Join-Path $mabiTestRoot 'bootstrap.exe')
New-Item -ItemType Directory -Path (Join-Path $mabiTestRoot 'repeat') | Out-Null
& "$PSScriptRoot/New-PortableHost.ps1" -ApplicationDirectory $mabiTestRoot -OutputPath (Join-Path $mabiTestRoot 'repeat/bootstrap.exe')
if((Get-FileHash -LiteralPath (Join-Path $mabiTestRoot 'bootstrap.exe') -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $mabiTestRoot 'repeat/bootstrap.exe') -Algorithm SHA256).Hash){throw 'Bootstrap compilation is not deterministic'}
foreach($mabiCase in @('success','corrupt','health-failure','interrupted','newer-than-base')){
    $mabiInstall=Join-Path $mabiTestRoot $mabiCase
    $mabiApp=Join-Path $mabiInstall 'App'
    $mabiDocs=Join-Path $mabiInstall 'Docs'
    $mabiConfig=Join-Path $mabiInstall 'Config'
    $mabiStage=Join-Path $mabiInstall 'Updates/staged'
    New-Item -ItemType Directory -Path $mabiApp,$mabiDocs,$mabiConfig,(Join-Path $mabiStage 'App'),(Join-Path $mabiStage 'Docs') | Out-Null
    Copy-Item -LiteralPath (Join-Path $mabiTestRoot 'bootstrap.exe') -Destination (Join-Path $mabiInstall 'MabiSync.exe')
    Copy-Item -LiteralPath $mabiFixtureExe -Destination (Join-Path $mabiApp 'MabiSync.exe')
    [IO.File]::WriteAllText((Join-Path $mabiApp 'MabiSync.dll'),'1.0.0')
    [IO.File]::WriteAllText((Join-Path $mabiApp 'obsolete.txt'),'old-only')
    [IO.File]::WriteAllText((Join-Path $mabiApp 'bootstrap-version.txt'),'1')
    [IO.File]::WriteAllText((Join-Path $mabiDocs 'guide.txt'),'old guide')
    [IO.File]::WriteAllText((Join-Path $mabiConfig '乐器列表.txt'),'user catalog')
    Copy-Item -LiteralPath $mabiFixtureExe -Destination (Join-Path $mabiStage 'App/MabiSync.exe')
    [IO.File]::WriteAllText((Join-Path $mabiStage 'App/MabiSync.dll'),$(if($mabiCase -eq 'health-failure'){'broken'}else{'1.0.1'}))
    [IO.File]::WriteAllText((Join-Path $mabiStage 'App/bootstrap-version.txt'),'1')
    [IO.File]::WriteAllText((Join-Path $mabiStage 'Docs/guide.txt'),'new guide')
    $mabiFiles=@(Get-ChildItem -LiteralPath $mabiStage -File -Recurse | ForEach-Object {@{path=[IO.Path]::GetRelativePath($mabiStage,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
    @{schema=1;product='MabiSync';version='1.0.1';bootstrapVersion=1;files=$mabiFiles} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $mabiInstall 'Updates/pending.json') -Encoding utf8NoBOM
    if($mabiCase -eq 'corrupt'){[IO.File]::WriteAllText((Join-Path $mabiStage 'App/MabiSync.dll'),'corrupt')}
    if($mabiCase -eq 'interrupted'){
        New-Item -ItemType Directory -Path (Join-Path $mabiInstall 'Updates/backup') | Out-Null
        Move-Item -LiteralPath $mabiApp -Destination (Join-Path $mabiInstall 'Updates/backup/App')
        Move-Item -LiteralPath (Join-Path $mabiStage 'App') -Destination $mabiApp
        [IO.File]::WriteAllText((Join-Path $mabiInstall 'Updates/applying'),'1.0.1')
    }
    if($mabiCase -eq 'newer-than-base'){
        # A corrupt target must not destroy a newer, working live application.
        [IO.File]::WriteAllText((Join-Path $mabiApp 'MabiSync.dll'),'1.0.2')
        [IO.File]::WriteAllText((Join-Path $mabiStage 'App/MabiSync.dll'),'corrupt')
    }
    $mabiProcess=Start-Process -FilePath (Join-Path $mabiInstall 'MabiSync.exe') -WindowStyle Hidden -PassThru
    if(!$mabiProcess.WaitForExit(20000)){$mabiProcess.Kill();throw ('Bootstrap hung in '+$mabiCase)}
    if($mabiProcess.ExitCode -ne 0){throw ('Bootstrap failed in '+$mabiCase)}
    $mabiExpected=if($mabiCase -eq 'success'){'1.0.1'}elseif($mabiCase -eq 'newer-than-base'){'1.0.2'}else{'1.0.0'}
    if((Get-Content -LiteralPath (Join-Path $mabiInstall 'launched.txt') -Raw) -ne $mabiExpected){throw ('Wrong version launched in '+$mabiCase)}
    if((Get-Content -LiteralPath (Join-Path $mabiConfig '乐器列表.txt') -Raw) -ne 'user catalog'){throw 'Custom instruments changed'}
    $mabiResult=Get-Content -LiteralPath (Join-Path $mabiInstall 'Updates/result.json') -Raw | ConvertFrom-Json
    if($mabiResult.success -ne ($mabiCase -eq 'success')){throw ('Incorrect update outcome in '+$mabiCase)}
    if(Test-Path -LiteralPath (Join-Path $mabiInstall 'Updates/pending.json')){throw 'Completed or rejected pending update remains'}
    if($mabiCase -eq 'success' -and ((Test-Path -LiteralPath (Join-Path $mabiApp 'obsolete.txt')) -or (Get-Content -LiteralPath (Join-Path $mabiDocs 'guide.txt') -Raw) -ne 'new guide')){throw 'Stale files survived directory replacement'}
    Write-Output ('PASS update bootstrap '+$mabiCase+'; desktop inputs: 0')
}
$mabiBaseline=Join-Path $mabiTestRoot 'corrupt'
$mabiTarget=Join-Path $mabiTestRoot 'success'
@{label='1.0.0'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $mabiBaseline 'App/package-manifest.json') -Encoding utf8NoBOM
@{label='1.0.1'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $mabiTarget 'App/package-manifest.json') -Encoding utf8NoBOM
$mabiPortable=Join-Path $mabiTestRoot 'MabiSync-1.0.1-win-x64.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($mabiTarget,$mabiPortable,[IO.Compression.CompressionLevel]::SmallestSize,$true)
& "$PSScriptRoot/New-UpdateAssets.ps1" -Directory $mabiTarget -Version 1.0.1 -PortableZip $mabiPortable -BaselineDirectory @($mabiBaseline) | Out-Null
$mabiDescriptor=Get-Content -LiteralPath (Join-Path $mabiTestRoot 'MabiSync-1.0.1-update-assets/MabiSync-update.json') -Raw | ConvertFrom-Json
$mabiPatch=$mabiDescriptor.packages | Where-Object {$_.fromVersion -eq '1.0.0'}
if(!$mabiPatch -or $mabiPatch.paths.Count -ne 3 -or !($mabiPatch.paths -contains 'App/MabiSync.dll') -or !($mabiPatch.paths -contains 'Docs/guide.txt') -or !($mabiPatch.paths -contains 'App/package-manifest.json')){throw 'Delta generator did not isolate changed files'}
if($mabiDescriptor.files.path -contains 'Config/乐器列表.txt' -or $mabiDescriptor.files.path -contains 'MabiSync.exe'){throw 'Descriptor includes user data or bootstrap'}
$mabiPatchZip=[IO.Compression.ZipFile]::OpenRead((Join-Path $mabiTestRoot ('MabiSync-1.0.1-update-assets/'+$mabiPatch.assetName)))
try{if($mabiPatchZip.Entries.Count -ne 3){throw 'Delta archive contains unchanged files'}}finally{$mabiPatchZip.Dispose()}
Write-Output 'PASS release delta generation includes only changed application and documentation files'
Write-Output ('Bootstrap fixtures: '+$mabiTestRoot)
