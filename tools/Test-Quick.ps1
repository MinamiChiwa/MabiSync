param()
$ErrorActionPreference = "Stop"
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    & dotnet build src/MabiSync.App -c Release
    if ($LASTEXITCODE -ne 0) { throw "Desktop build failed" }
    & dotnet build tests/MabiSync.Client.Probe -c Release
    if ($LASTEXITCODE -ne 0) { throw "Native integration probe build failed" }
    & dotnet build tests/MabiSync.App.Probe -c Release
    if ($LASTEXITCODE -ne 0) { throw "Desktop integration probe build failed" }
    & dotnet run --project tests/MabiSync.Core.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw "Core checks failed" }
    & dotnet run --project tests/MabiSync.Observation.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw "Observation checks failed" }
    & dotnet run --project tests/MabiSync.Input.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw "Input boundary checks failed" }
    & dotnet run --project tests/MabiSync.Network.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw "Public connection lifecycle checks failed" }
    & "$PSScriptRoot/Test-OnlineUpdate.ps1"
    if ($LASTEXITCODE -ne 0) { throw "Online update bootstrap checks failed" }
    & node --test tests/server.test.mjs tests/ensemble-server.test.mjs tests/client.integration.mjs tests/client.edge.integration.mjs tests/ui-contract.test.mjs tests/app.integration.mjs tests/timing.integration.mjs
    if ($LASTEXITCODE -ne 0) { throw "Room/client checks failed" }
}
finally { Pop-Location }
