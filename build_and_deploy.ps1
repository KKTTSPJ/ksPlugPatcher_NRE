# Build ksPlugPatcher_NRE (Koikatsu Sunshine only) and copy the DLL to ..\Deploy\ksPlugPatcher_NRE.
# Place this folder directly under the game folder (the project references ..\..\BepInEx and ..\..\KoikatsuSunshine_Data).
# Requires the .NET SDK.

$ErrorActionPreference = "Stop"

$Project = "$PSScriptRoot\ksPlugPatcher_NRE\ksPlugPatcher_NRE.csproj"
$DeployDir = "$PSScriptRoot\..\Deploy\ksPlugPatcher_NRE"
$Dll = "$PSScriptRoot\ksPlugPatcher_NRE\bin\Release_KKS\net46\ksPlugPatcher_NRE.dll"

if (-not (Test-Path $DeployDir)) {
    New-Item -ItemType Directory -Force -Path $DeployDir | Out-Null
}

Write-Host "Building Release_KKS..." -ForegroundColor Cyan
dotnet build $Project --configuration Release_KKS
if ($LASTEXITCODE -ne 0) {
    Write-Error "Release_KKS Build failed!"
    exit 1
}

if (-not (Test-Path $Dll)) {
    Write-Error "ksPlugPatcher_NRE.dll not found at $Dll!"
    exit 1
}
Copy-Item $Dll -Destination $DeployDir -Force
Write-Host "Deployed: ksPlugPatcher_NRE.dll -> $DeployDir" -ForegroundColor Green
