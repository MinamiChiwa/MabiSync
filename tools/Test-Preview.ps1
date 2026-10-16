#requires -Version 7.4
param([Parameter(Mandatory=$true)][string]$Directory)
$ErrorActionPreference = 'Stop'
$mabiPackage = (Resolve-Path -LiteralPath $Directory).Path
$mabiApp = Join-Path $mabiPackage 'App'
if(!(Test-Path -LiteralPath (Join-Path $mabiPackage 'Config/乐器列表.txt') -PathType Leaf)){throw 'Missing user instrument catalog'}
$mabiRequired = @('MabiSync.exe','App/MabiSync.dll','App/MabiSync.runtimeconfig.json','App/hostfxr.dll','App/coreclr.dll','App/PresentationFramework.dll','App/runtime/node.exe','App/Service/server/index.js','App/Service/server/room-server.js','App/Service/server/ensemble-observation.js','App/Service/node_modules/ws/package.json','App/Service/node_modules/ws/LICENSE','Docs/THIRD-PARTY/node-LICENSE.txt','Docs/使用说明.txt','App/package-manifest.json')
foreach ($mabiRelative in $mabiRequired) {
    if (!(Test-Path -LiteralPath (Join-Path $mabiPackage $mabiRelative) -PathType Leaf)) { throw ('Missing package file: '+$mabiRelative) }
}
$mabiRootFiles = @(Get-ChildItem -LiteralPath $mabiPackage -File)
if ($mabiRootFiles.Count -ne 1 -or $mabiRootFiles[0].Name -ne 'MabiSync.exe') { throw 'Package root must contain only its launcher and directories' }
$mabiRuntime = Get-Content -LiteralPath (Join-Path $mabiApp 'MabiSync.runtimeconfig.json') -Raw | ConvertFrom-Json
if (!$mabiRuntime.runtimeOptions.includedFrameworks) { throw 'Package is not self-contained' }
$mabiManifest = Get-Content -LiteralPath (Join-Path $mabiApp 'package-manifest.json') -Raw | ConvertFrom-Json
if($mabiManifest.npcapBundled -ne $false -or @(Get-ChildItem -LiteralPath $mabiPackage -File -Recurse | Where-Object {$_.Name -match '^(wpcap|Packet)\.dll$|^npcap.*\.(exe|msi|sys)$'}).Count){throw 'Npcap must remain an external dependency'}
foreach($mabiLicenseFile in @('Microsoft.NETCore.App-LICENSE.TXT','Microsoft.NETCore.App-THIRD-PARTY-NOTICES.TXT','Microsoft.WindowsDesktop.App-LICENSE')){
    if(!(Test-Path -LiteralPath (Join-Path $mabiPackage ('Docs/THIRD-PARTY/'+$mabiLicenseFile)))){throw ('Missing runtime notice: '+$mabiLicenseFile)}
}
if(@(Get-ChildItem -LiteralPath $mabiPackage -File -Recurse | Where-Object {$_.Name -match 'easytier'}).Count){throw 'Removed EasyTier components are still bundled'}
if ($mabiManifest.layoutVersion -ne 2) { throw 'Unexpected portable layout version' }
if ($mabiManifest.product -ne 'MabiSync' -or $mabiManifest.architecture -ne 'win-x64') { throw 'Unexpected product or architecture' }
if (!$mabiManifest.tunnelIncluded) { throw 'Public-room preview is missing its cloudflared runtime' }
if ($mabiManifest.tunnelIncluded) {
    foreach ($mabiRuntimeFile in @('App/runtime/cloudflared.exe','Docs/THIRD-PARTY/cloudflared-LICENSE.txt')) {
        if (!(Test-Path -LiteralPath (Join-Path $mabiPackage $mabiRuntimeFile) -PathType Leaf)) { throw ('Missing public connection runtime: '+$mabiRuntimeFile) }
    }
    $mabiTunnelVersion = & (Join-Path $mabiApp 'runtime/cloudflared.exe') --version
    if ($LASTEXITCODE -ne 0 -or $mabiTunnelVersion -notlike ('*'+$mabiManifest.cloudflaredVersion+'*')) { throw 'Bundled cloudflared version differs from manifest' }
}
$mabiEntries = @{}
foreach ($mabiFile in $mabiManifest.files) {
    if ($mabiFile.path -match '(^/|^[A-Za-z]:|(^|/)\.\.(/|$)|\\)' -or $mabiEntries.ContainsKey($mabiFile.path)) { throw 'Invalid package manifest path' }
    $mabiEntries[$mabiFile.path] = $true
    $mabiTarget = Join-Path $mabiPackage $mabiFile.path
    if (!(Test-Path -LiteralPath $mabiTarget -PathType Leaf) -or (Get-FileHash -LiteralPath $mabiTarget -Algorithm SHA256).Hash -ne $mabiFile.sha256) { throw ('Package hash mismatch: '+$mabiFile.path) }
}
$mabiActual = @(Get-ChildItem -LiteralPath $mabiPackage -File -Recurse | Where-Object { $_.Name -ne 'package-manifest.json' })
if ($mabiActual.Count -ne $mabiEntries.Count) { throw 'Package has unlisted files' }
if (@(Get-ChildItem -LiteralPath $mabiPackage -Recurse -File | Where-Object { $_.Extension -in @('.pcap','.pcapng','.pdb') -or $_.Name -eq 'settings.json' }).Count -gt 0) { throw 'Private or development files included' }
$mabiSmokePath = Join-Path ([IO.Path]::GetTempPath()) ('mabisync-package-smoke-'+[guid]::NewGuid().ToString('N')+'.mjs')
$mabiBootReport = [IO.Path]::ChangeExtension($mabiSmokePath, '.json')
try {
    $mabiLaunch = [Diagnostics.ProcessStartInfo]::new((Join-Path $mabiPackage 'MabiSync.exe'))
    $mabiLaunch.UseShellExecute = $false; $mabiLaunch.CreateNoWindow = $true
    $mabiLaunch.WorkingDirectory = [IO.Path]::GetTempPath()
    $mabiLaunch.ArgumentList.Add('--check-package'); $mabiLaunch.ArgumentList.Add($mabiBootReport)
    $mabiLaunch.Environment['DOTNET_ROOT'] = Join-Path ([IO.Path]::GetTempPath()) 'mabisync-no-shared-runtime'
    $mabiLaunch.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $mabiProcess = [Diagnostics.Process]::Start($mabiLaunch)
    try {
        if (!$mabiProcess.WaitForExit(15000)) { $mabiProcess.Kill($true); throw 'Portable apphost launch timed out' }
        if ($mabiProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath $mabiBootReport)) { throw 'Portable root launcher did not start the bundled WPF runtime' }
    } finally { $mabiProcess.Dispose() }
    $mabiBoot = Get-Content -LiteralPath $mabiBootReport -Raw | ConvertFrom-Json
    if (!$mabiBoot.started -or $mabiBoot.gameInputs -ne 0 -or $mabiBoot.instrumentCount -lt 14 -or $mabiBoot.baseDirectory.TrimEnd('\','/') -ne $mabiApp.TrimEnd('\','/')) { throw 'Root launcher resolved an unexpected application directory or missing catalog' }
    @'
import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';
import path from 'node:path';
const { createRoomServer } = await import(pathToFileURL(path.join(process.argv[2], 'Service/server/room-server.js')));
assert.equal(process.arch, 'x64');
const service = await createRoomServer({ port: 0 });
try {
  const response = await fetch(service.url.replace('ws:', 'http:') + '/health');
  assert.equal(response.status, 200);
  const health = await response.json();
  assert.equal(health.product, 'MabiSync');
  assert.equal(health.protocolVersion, 1);
  assert.equal(health.features?.gameObservation, 1);
  assert.equal(health.features?.ensembleRoster, 1);
  assert.equal(health.features?.firstCycleStop, 1);
  assert.equal(health.features?.rosterEditing, 1);
  assert.equal(health.features?.roomClosure, 1);
  assert.equal(health.features?.adaptiveTiming, 2);
  assert.match(health.serverEpoch, /^[0-9a-f-]{36}$/);
  console.log(JSON.stringify({ packagedServiceHealthy: true, node: process.version, architecture: process.arch, gameInputSent: false }));
} finally { await service.close(); }
'@ | Set-Content -LiteralPath $mabiSmokePath -Encoding utf8
    & (Join-Path $mabiApp 'runtime/node.exe') $mabiSmokePath $mabiApp
    if ($LASTEXITCODE -ne 0) { throw 'Bundled room service smoke test failed' }
    Write-Output ('PASS portable preview integrity and service: '+$mabiPackage)
}
finally { Remove-Item -LiteralPath $mabiSmokePath,$mabiBootReport -ErrorAction SilentlyContinue }
