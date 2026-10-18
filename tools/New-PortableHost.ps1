#requires -Version 7.4
param([Parameter(Mandatory=$true)][string]$ApplicationDirectory, [Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
$mabiAppDirectory = (Resolve-Path -LiteralPath $ApplicationDirectory).Path
$mabiDotnetRoot = Split-Path -Parent (Get-Command dotnet -ErrorAction Stop).Source
$mabiSdkVersion = (& dotnet --version).Trim()
$mabiCompiler = Join-Path $mabiDotnetRoot ('sdk/'+$mabiSdkVersion+'/Roslyn/bincore/csc.dll')
$mabiFramework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
if (!(Test-Path -LiteralPath $mabiCompiler)) { throw 'The .NET SDK C# compiler is required to build the portable bootstrap' }
$mabiSource = Join-Path $PSScriptRoot '../src/MabiSync.Bootstrap/Program.cs'
$mabiIcon = Join-Path $PSScriptRoot '../src/MabiSync.App/Assets/MabiSync.ico'
$mabiArguments=@($mabiCompiler,'/nologo','/noconfig','/nostdlib+','/deterministic+','/target:winexe','/platform:x64','/optimize+',('/pathmap:'+[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))+'=/_/'))
foreach($mabiReference in @('mscorlib.dll','System.dll','System.Core.dll','System.Web.Extensions.dll','System.Windows.Forms.dll')){$mabiArguments+=('/reference:'+(Join-Path $mabiFramework $mabiReference))}
$mabiArguments+=@("/win32icon:$mabiIcon","/out:$([IO.Path]::GetFullPath($OutputPath))",[IO.Path]::GetFullPath($mabiSource))
& dotnet @mabiArguments
if ($LASTEXITCODE -ne 0) { throw 'Portable bootstrap compilation failed' }
[IO.File]::WriteAllText((Join-Path $mabiAppDirectory 'bootstrap-version.txt'),'1')
