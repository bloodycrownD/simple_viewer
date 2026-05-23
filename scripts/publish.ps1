# Publishes viewer.exe (Release, win-x64, self-contained Windows App SDK runtime).
# Output folder: bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot

Push-Location $Root
try {
    dotnet publish SimpleViewer.csproj `
        -c Release `
        -r win-x64 `
        -p:Platform=x64 `
        -p:WindowsAppSDKSelfContained=true `
        -p:EnableCoreMrtTooling=false `
        --self-contained false `
        -p:PublishSingleFile=false
}
finally {
    Pop-Location
}
