
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
        $exe = Join-Path $projectDir 'bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\Se7enPro.exe'
    } else {
        dotnet build -c Release -r win-x64 --self-contained false
        $exe = Join-Path $projectDir 'bin\Release\net8.0-windows10.0.19041.0\win-x64\Se7enPro.exe'
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

