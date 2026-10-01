<#
 Builds the distribution archive for Se7en Pro.

 The default output is self-contained: it carries the .NET runtime, so a machine that has
 never installed anything still starts the app. That is the "with_dotnet" asset on the
 release page, and it is the one the in-app updater picks. Pass -FrameworkDependent only
 when you deliberately want the smaller build that needs a matching Desktop Runtime.
#>
param(
    [ValidateSet('x64', 'x86')]
    [string]$Arch = 'x64',

    [switch]$FrameworkDependent,

    [string]$OutDir = 'dist'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'Se7enPro\Se7enPro.csproj'
$version = (dotnet msbuild $project -getProperty:Version -nologo | Select-Object -Last 1).Trim()
if (-not $version) { throw 'Could not read <Version> from the project file.' }

$suffix = if ($FrameworkDependent) { 'without_dotnet' } else { 'with_dotnet' }
$name = "Se7enPro_v${version}_Portable_x64_${suffix}"
if ($Arch -eq 'x86') { $name = $name -replace '_x64_', '_x86_' }

$stage = Join-Path $root "$OutDir\$name"
$zip = Join-Path $root "$OutDir\$name.zip"

Write-Host "Publishing $name ..." -ForegroundColor Cyan
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

dotnet publish $project `
    -c Release `
    -r "win-$Arch" `
    --nologo `
    $(if ($FrameworkDependent) { '--no-self-contained' } else { '--self-contained' }) `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$exe = Join-Path $stage 'Se7enPro.exe'
if (-not (Test-Path $exe)) { throw "Se7enPro.exe is missing from $stage - the archive would be unusable." }

New-Item -ItemType Directory -Path (Join-Path $root $OutDir) -Force | Out-Null
if (Test-Path $zip) { Remove-Item $zip -Force }

Write-Host 'Compressing...' -ForegroundColor Cyan

# Compress-Archive writes Windows backslashes into the entry names, which the
# ZIP format does not specify and non-Windows extractors mishandle.
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$stageFull = (Resolve-Path $stage).Path.TrimEnd('\') + '\'
$archive = [System.IO.Compression.ZipFile]::Open(
    $zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in [System.IO.Directory]::EnumerateFiles($stageFull, '*', 'AllDirectories')) {
        $entryName = $file.Substring($stageFull.Length).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $file, $entryName,
            [System.IO.Compression.CompressionLevel]::Optimal)
    }
}
finally {
    $archive.Dispose()
}

$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Ready: $zip ($mb MB)" -ForegroundColor Green
