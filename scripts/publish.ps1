# Publishes viewer.exe (Release, win-x64, framework-dependent single file).
# Output: bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/viewer.exe

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot

Push-Location $Root
try {
    dotnet publish SimpleViewer.csproj `
        -c Release `
        -r win-x64 `
        -p:Platform=x64 `
        --self-contained false `
        -p:PublishSingleFile=true
}
finally {
    Pop-Location
}
