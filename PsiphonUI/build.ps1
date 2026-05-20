# PsiphonUI build helper.
#
# Usage:
#   .\build.ps1              # Restore + build (Release, framework-dependent).
#   .\build.ps1 -Publish     # Publish a single, self-contained, compressed exe.
#   .\build.ps1 -Run         # Build then launch PsiphonUI.exe.
param(
    [switch]$Publish,
    [switch]$Run
)

$ErrorActionPreference = "Stop"

$projectDir = $PSScriptRoot
Push-Location $projectDir
try {
    if ($Publish) {
        dotnet publish -c Release -r win-x64 `
            --self-contained true `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true
        $exe = Join-Path $projectDir 'bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\PsiphonUI.exe'
    } else {
        dotnet build -c Release -r win-x64 --self-contained false
        $exe = Join-Path $projectDir 'bin\Release\net8.0-windows10.0.19041.0\win-x64\PsiphonUI.exe'
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Build failed with exit code $LASTEXITCODE"
    }

    Write-Host "`nOutput: $exe" -ForegroundColor Green

    if ($Run) {
        & $exe
    }
}
finally {
    Pop-Location
}
