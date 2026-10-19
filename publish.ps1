# Builds a single portable MongoDBTinyClient.exe (no .NET runtime required)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet publish -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -o publish
    Write-Host ""
    Write-Host "Ready: $(Join-Path $PSScriptRoot 'publish\MongoDBTinyClient.exe')" -ForegroundColor Green
}
finally { Pop-Location }
