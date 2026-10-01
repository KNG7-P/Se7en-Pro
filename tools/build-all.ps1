<#
.SYNOPSIS
    Builds clean Portable archives and Inno Setup installers for Se7en Pro (x64 and x86).
.PARAMETER Arch
    Target architecture: 'all', 'x64', or 'x86'. Default is 'all'.
.PARAMETER FrameworkDependent
    Builds without bundling the .NET runtime (requires .NET Desktop Runtime on target machine).
.PARAMETER AllVariants
    Builds both with-dotnet (Self-Contained) and without-dotnet (Framework-Dependent) variants.
.PARAMETER SkipInstaller
    Skips generating the Inno Setup installer executable.
.PARAMETER SkipPortable
    Skips generating the portable .zip archive.
#>
param(
    [ValidateSet('all', 'x64', 'x86')]
    [string]$Arch = 'all',

    [switch]$FrameworkDependent,
    [switch]$AllVariants,
    [switch]$SkipInstaller,
    [switch]$SkipPortable,

    [string]$OutDir = 'dist'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'Se7enPro\Se7enPro.csproj'
$distDir = Join-Path $root $OutDir

# 1. Read app version from project file
$version = (dotnet msbuild $project -getProperty:Version -nologo | Select-Object -Last 1).Trim()
if (-not $version) {
    throw "Could not determine Version from $project"
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Se7en Pro Build Pipeline - Version: v$version" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 2. Locate Inno Setup Compiler if installer is requested
$iscc = $null
if (-not $SkipInstaller) {
    $possiblePaths = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 5\ISCC.exe"
    )
    foreach ($p in $possiblePaths) {
        if (Test-Path $p) { $iscc = $p; break }
    }
    if (-not $iscc) {
        $cmd = Get-Command iscc -ErrorAction SilentlyContinue
        if ($cmd) { $iscc = $cmd.Source }
    }
    if (-not $iscc) {
        Write-Warning "Inno Setup compiler (ISCC.exe) not found. Installer generation will be skipped."
        $SkipInstaller = $true
    } else {
        Write-Host "Using Inno Setup Compiler: $iscc" -ForegroundColor Gray
    }
}

# 3. Determine architectures and variants
$architectures = if ($Arch -eq 'all') { @('x64', 'x86') } else { @($Arch) }
$variants = if ($AllVariants) {
    @($false, $true)
} elseif ($FrameworkDependent) {
    @($true)
} else {
    @($false)
}

New-Item -ItemType Directory -Path $distDir -Force | Out-Null

# Compress-Archive writes Windows backslashes into the entry names, which is
# not what the ZIP format specifies and trips up non-Windows extractors. Build
# the archive with ZipFile instead so every entry uses forward slashes.
function Compress-DirectoryToZip {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Add-Type -AssemblyName System.IO.Compression
    $sourceFull = (Resolve-Path $Source).Path.TrimEnd('\') + '\'

    if (Test-Path $Destination) { Remove-Item $Destination -Force }
    $archive = [System.IO.Compression.ZipFile]::Open(
        $Destination, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in [System.IO.Directory]::EnumerateFiles($sourceFull, '*', 'AllDirectories')) {
            $entryName = $file.Substring($sourceFull.Length).Replace('\', '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $file, $entryName,
                [System.IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally {
        $archive.Dispose()
    }
}

foreach ($isFd in $variants) {
    $suffix = if ($isFd) { 'without_dotnet' } else { 'with_dotnet' }

    foreach ($a in $architectures) {
        Write-Host "`n----------------------------------------------------------" -ForegroundColor Yellow
        Write-Host "  Building for Windows ($a) [$suffix] ..." -ForegroundColor Yellow
        Write-Host "----------------------------------------------------------" -ForegroundColor Yellow

        $staging = Join-Path $distDir "staging-$a-$suffix"
        if (Test-Path $staging) {
            Remove-Item $staging -Recurse -Force
        }

        # Publish build
        Write-Host "Publishing release (win-$a, $suffix)..." -ForegroundColor Cyan
        
        if ($isFd) {
            dotnet publish $project `
                -c Release `
                -r "win-$a" `
                --nologo `
                --no-self-contained `
                -p:PublishSingleFile=false `
                -p:DebugType=none `
                -p:DebugSymbols=false `
                -o $staging
        } else {
            dotnet publish $project `
                -c Release `
                -r "win-$a" `
                --nologo `
                --self-contained `
                -p:PublishSingleFile=false `
                -p:DebugType=none `
                -p:DebugSymbols=false `
                -o $staging
        }

        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed for win-$a with exit code $LASTEXITCODE."
        }

        $exe = Join-Path $staging 'Se7enPro.exe'
        if (-not (Test-Path $exe)) {
            throw "Se7enPro.exe was not produced in $staging."
        }

        # Generate Portable ZIP
        if (-not $SkipPortable) {
            $zipName = if ($isFd) {
                "Se7enPro_v${version}_Portable_${a}_without_dotnet.zip"
            } else {
                "Se7enPro_v${version}_Portable_${a}.zip"
            }
            $zipPath = Join-Path $distDir $zipName
            if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

            Write-Host "Compressing Portable Archive: $zipName ..." -ForegroundColor Cyan
            Compress-DirectoryToZip -Source $staging -Destination $zipPath
            $zipMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
            Write-Host "[OK] Portable Archive created: $zipName ($zipMb MB)" -ForegroundColor Green
        }

        # Generate Inno Setup Installer
        if (-not $SkipInstaller -and $iscc) {
            $issFile = Join-Path $root "installer\installer_${a}.iss"
            if (-not (Test-Path $issFile)) {
                Write-Warning "Installer script not found: $issFile"
            } else {
                $baseName = if ($isFd) {
                    "Se7enPro_v${version}_Setup_${a}_without_dotnet"
                } else {
                    "Se7enPro_v${version}_Setup_${a}"
                }
                Write-Host "Compiling Inno Setup Installer: $baseName.exe ..." -ForegroundColor Cyan
                $args = @(
                    "`"/DMyAppVersion=$version`"",
                    "`"/DOutputBaseFilename=$baseName`"",
                    "`"/DSourcePath=$staging`"",
                    "`"/DOutputDir=$distDir`"",
                    "`"$issFile`""
                )
                $pinfo = New-Object System.Diagnostics.ProcessStartInfo
                $pinfo.FileName = $iscc
                $pinfo.Arguments = $args -join ' '
                $pinfo.UseShellExecute = $false
                $pinfo.RedirectStandardOutput = $true
                $pinfo.RedirectStandardError = $true

                $proc = [System.Diagnostics.Process]::Start($pinfo)
                $stdout = $proc.StandardOutput.ReadToEnd()
                $stderr = $proc.StandardError.ReadToEnd()
                $proc.WaitForExit()

                if ($proc.ExitCode -ne 0) {
                    Write-Host $stdout
                    Write-Error $stderr
                    throw "ISCC failed with exit code $($proc.ExitCode)"
                }

                $setupName = "$baseName.exe"
                $setupPath = Join-Path $distDir $setupName
                if (Test-Path $setupPath) {
                    $setupMb = [math]::Round((Get-Item $setupPath).Length / 1MB, 2)
                    Write-Host "[OK] Setup Installer created: $setupName ($setupMb MB)" -ForegroundColor Green
                } else {
                    Write-Warning "Setup output expected at $setupPath was not found."
                }
            }
        }

        # Cleanup staging directory
        if (Test-Path $staging) {
            Remove-Item $staging -Recurse -Force
        }
    }
}

Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host "  Build Pipeline Completed Successfully!" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green

Get-ChildItem -Path $distDir -File | Select-Object Name, @{Name="Size (MB)"; Expression={[math]::Round($_.Length / 1MB, 2)}}, LastWriteTime | Format-Table -AutoSize
