#requires -Version 7.4
param([string]$NodePath, [string]$CloudflaredPath, [string]$NodeLicensePath, [string]$CloudflaredLicensePath, [string]$Label='0.1.5', [switch]$SkipChecks, [switch]$Release, [string[]]$UpdateBaselineDirectory=@())
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    if ($Label -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]{0,60}$') { throw 'Invalid preview label' }
    if (!$NodePath) { $NodePath = (Get-Command node -ErrorAction Stop).Source }
    $NodePath = (Resolve-Path -LiteralPath $NodePath).Path
    $mabiNodeVersion = (& $NodePath --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $mabiNodeVersion -notmatch '^v(\d+)\.\d+\.\d+$' -or [int]$Matches[1] -lt 20) { throw 'Node.js 20+ required' }
    $mabiArch = (& $NodePath -p 'process.arch').Trim()
    if ($LASTEXITCODE -ne 0 -or $mabiArch -ne 'x64') { throw 'Windows x64 Node runtime required' }
    if (!$CloudflaredPath) {
        $mabiTunnelCommand = Get-Command cloudflared.exe -ErrorAction SilentlyContinue
        if ($mabiTunnelCommand) { $CloudflaredPath = $mabiTunnelCommand.Source }
        else {
            $mabiExistingTunnel = Get-ChildItem -LiteralPath 'outputs/previews' -Filter cloudflared.exe -File -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.Directory.Name -eq 'runtime' } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
            if ($mabiExistingTunnel) { $CloudflaredPath = $mabiExistingTunnel.FullName }
        }
    }
    if (!$CloudflaredPath) { throw 'cloudflared.exe is required for public rooms. Supply -CloudflaredPath or install it on PATH.' }
    $CloudflaredPath = (Resolve-Path -LiteralPath $CloudflaredPath).Path
    if (!$SkipChecks) { & "$PSScriptRoot/Test-Quick.ps1"; if ($LASTEXITCODE -ne 0) { throw 'Quick regression failed' } }
    $mabiOutput = Join-Path (Get-Location).Path $(if($Release){'outputs/releases/MabiSync-'+$Label+'-win-x64'}else{'outputs/previews/MabiSync-'+$Label+'-win-x64-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,6)})
    if (Test-Path -LiteralPath $mabiOutput) { throw 'Output must be a fresh directory' }
    New-Item -ItemType Directory -Path $mabiOutput | Out-Null
    # Preserve the exact runtime license matching the local executable; no runtime installer is downloaded or executed.
    $mabiApp = Join-Path $mabiOutput 'App'
    $mabiDocs = Join-Path $mabiOutput 'Docs'
    $mabiThirdParty = Join-Path $mabiDocs 'THIRD-PARTY'
    New-Item -ItemType Directory -Path $mabiThirdParty | Out-Null
    $mabiLicenseUrl = 'https://raw.githubusercontent.com/nodejs/node/'+$mabiNodeVersion+'/LICENSE'
    if($NodeLicensePath){Copy-Item -LiteralPath (Resolve-Path -LiteralPath $NodeLicensePath).Path -Destination (Join-Path $mabiThirdParty 'node-LICENSE.txt')}
    else{Invoke-WebRequest -UseBasicParsing -Uri $mabiLicenseUrl -OutFile (Join-Path $mabiThirdParty 'node-LICENSE.txt')}
    if ((Get-Item -LiteralPath (Join-Path $mabiThirdParty 'node-LICENSE.txt')).Length -lt 1000) { throw 'Node license response is incomplete' }
    $mabiTunnelVersion = $null
    if ($CloudflaredPath) {
        $CloudflaredPath = (Resolve-Path -LiteralPath $CloudflaredPath).Path
        $mabiTunnelVersionText = & $CloudflaredPath --version
        if ($LASTEXITCODE -ne 0 -or $mabiTunnelVersionText -notmatch 'cloudflared version (\d{4}\.\d+\.\d+)') { throw 'Unsupported cloudflared runtime' }
        $mabiTunnelVersion = $Matches[1]
        if($CloudflaredLicensePath){Copy-Item -LiteralPath (Resolve-Path -LiteralPath $CloudflaredLicensePath).Path -Destination (Join-Path $mabiThirdParty 'cloudflared-LICENSE.txt')}
        else{Invoke-WebRequest -UseBasicParsing -Uri ('https://raw.githubusercontent.com/cloudflare/cloudflared/'+$mabiTunnelVersion+'/LICENSE') -OutFile (Join-Path $mabiThirdParty 'cloudflared-LICENSE.txt')}
        if ((Get-Item -LiteralPath (Join-Path $mabiThirdParty 'cloudflared-LICENSE.txt')).Length -lt 1000) { throw 'Cloudflared license response is incomplete' }
    }
    & dotnet publish src/MabiSync.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $mabiApp
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed; partial output preserved for diagnosis' }
    # Match runtime notices to the exact restored frameworks, without adding installers.
    $mabiNugetRoot=if($env:NUGET_PACKAGES){$env:NUGET_PACKAGES}else{Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'}
    $mabiFrameworks=(Get-Content -LiteralPath (Join-Path $mabiApp 'MabiSync.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks
    foreach($mabiFramework in $mabiFrameworks){
        $mabiFrameworkRoot=Join-Path $mabiNugetRoot ($mabiFramework.name.ToLowerInvariant()+'.runtime.win-x64/'+$mabiFramework.version)
        $mabiLicenses=@(Get-ChildItem -LiteralPath $mabiFrameworkRoot -File | Where-Object {$_.Name -match '^(LICENSE(\.TXT)?|THIRD-PARTY-NOTICES\.TXT)$'})
        if(!$mabiLicenses.Count){throw ('Missing framework license: '+$mabiFramework.name)}
        foreach($mabiLicense in $mabiLicenses){Copy-Item -LiteralPath $mabiLicense.FullName -Destination (Join-Path $mabiThirdParty ($mabiFramework.name+'-'+$mabiLicense.Name))}
    }
    & "$PSScriptRoot/New-PortableHost.ps1" -ApplicationDirectory $mabiApp -OutputPath (Join-Path $mabiOutput 'MabiSync.exe')
    New-Item -ItemType Directory -Path (Join-Path $mabiOutput 'Config') | Out-Null
    Copy-Item -LiteralPath (Join-Path $mabiApp 'Config/乐器列表.txt') -Destination (Join-Path $mabiOutput 'Config/乐器列表.txt')
    Remove-Item -LiteralPath (Join-Path $mabiApp 'Config/乐器列表.txt')
    Remove-Item -LiteralPath (Join-Path $mabiApp 'Config')
    New-Item -ItemType Directory -Path (Join-Path $mabiApp 'runtime') | Out-Null
    Copy-Item -LiteralPath $NodePath -Destination (Join-Path $mabiApp 'runtime/node.exe')
    if ($CloudflaredPath) { Copy-Item -LiteralPath $CloudflaredPath -Destination (Join-Path $mabiApp 'runtime/cloudflared.exe') }
    Copy-Item -LiteralPath 'docs/guide.zh-CN.txt' -Destination (Join-Path $mabiDocs '使用说明.txt')
    Copy-Item -LiteralPath 'THIRD_PARTY_NOTICES.md' -Destination (Join-Path $mabiDocs '第三方来源.md')
    $mabiCommit = (& git rev-parse HEAD).Trim()
    $mabiDirty = [bool](& git status --porcelain)
    $mabiFileList = @(Get-ChildItem -LiteralPath $mabiOutput -Recurse -File | Sort-Object FullName | ForEach-Object {
        @{ path=$_.FullName.Substring($mabiOutput.Length+1).Replace('\','/'); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    @{ product='MabiSync'; label=$Label; architecture='win-x64'; layoutVersion=2; sourceCommit=$mabiCommit; sourceDirty=$mabiDirty; nodeVersion=$mabiNodeVersion; tunnelIncluded=[bool]$CloudflaredPath; cloudflaredVersion=$mabiTunnelVersion; defaultProvider='community'; defaultServer='wss://47.111.3.134/'; npcapBundled=$false; createdUtc=[DateTime]::UtcNow.ToString('o'); defaultSimulation=$false; gameFirstCycleStopVerified=$false; files=$mabiFileList } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $mabiApp 'package-manifest.json') -Encoding utf8
    & "$PSScriptRoot/Test-Preview.ps1" -Directory $mabiOutput
    if ($LASTEXITCODE -ne 0) { throw 'Package verification failed' }
    $mabiZip = $mabiOutput+'.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($mabiOutput,$mabiZip,[IO.Compression.CompressionLevel]::SmallestSize,$true)
    $mabiZipHash=(Get-FileHash -LiteralPath $mabiZip -Algorithm SHA256).Hash
    ($mabiZipHash+'  '+[IO.Path]::GetFileName($mabiZip)) | Set-Content -LiteralPath ($mabiZip+'.sha256') -Encoding ascii
    if($Release){ & "$PSScriptRoot/New-UpdateAssets.ps1" -Directory $mabiOutput -Version $Label -PortableZip $mabiZip -BaselineDirectory $UpdateBaselineDirectory }
    $mabiLatest=if($Release){'outputs/latest-release.json'}else{'outputs/latest-preview.json'}
    @{ directory=$mabiOutput; zip=$mabiZip; sha256=$mabiZipHash; label=$Label; packageChecked=$true; cleanMachineLaunchVerified=$false } | ConvertTo-Json | Set-Content -LiteralPath $mabiLatest -Encoding utf8
    Write-Output ('Preview package: '+$mabiZip)
}
finally { Pop-Location }
