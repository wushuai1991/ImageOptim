# ImageOptim Windows tool download script.
# Called by MSBuild during build to download Windows prebuilt binaries of each
# image optimizer tool into the Tools directory.
# Download failures only produce warnings and do not block the build
# (users can manually place .exe files into the Tools directory).
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File download-tools.ps1 -Destination <ToolsDir>

param(
    [string]$Destination = ""
)

$ErrorActionPreference = "Continue"

if ([string]::IsNullOrEmpty($Destination)) {
    $Destination = Join-Path $PSScriptRoot "..\ImageOptim\Tools"
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
Write-Host "Tool output directory: $Destination"

# Each tool entry: name, file name, download URL list (tried in priority order)
$tools = @(
    @{
        Name = "oxipng"
        File = "oxipng.exe"
        Urls = @(
            "https://github.com/oxipng/oxipng/releases/download/v9.1.4/oxipng-9.1.4-x86_64-pc-windows-msvc.zip"
        )
        IsZip = $true
    },
    @{
        Name = "pngquant"
        File = "pngquant.exe"
        Urls = @(
            "https://pngquant.org/pngquant-windows.zip"
        )
        IsZip = $true
    },
    @{
        Name = "zopflipng"
        File = "zopflipng.exe"
        Urls = @(
            "https://github.com/imagemin/zopflipng-bin/raw/main/vendor/win32/x64/zopflipng.exe"
        )
        IsZip = $false
    },
    @{
        Name = "jpegoptim"
        File = "jpegoptim.exe"
        Urls = @(
            "https://github.com/tjko/jpegoptim/releases/download/v1.5.6/jpegoptim-1.5.6-x64-windows.zip"
        )
        IsZip = $true
    },
    @{
        # jpegli (preferred JPEG tool): extract cjpegli.exe from the official libjxl Windows static package
        Name = "cjpegli"
        File = "cjpegli.exe"
        Urls = @(
            "https://github.com/libjxl/libjxl/releases/download/v0.11.1/jxl-x64-windows-static.zip"
        )
        IsZip = $true
        ZipExeName = "cjpegli.exe"
    },
    @{
        # MozJPEG official Windows static jpegtran (secondary): extract static/Release/jpegtran-static.exe from mozjpeg-v4.0.3-win-x64.zip
        Name = "jpegtran"
        File = "jpegtran.exe"
        Urls = @(
            "https://github.com/mozilla/mozjpeg/releases/download/v4.0.3/mozjpeg-v4.0.3-win-x64.zip"
        )
        IsZip = $true
        ZipExeName = "jpegtran-static.exe"
    },
    @{
        Name = "gifsicle"
        File = "gifsicle.exe"
        Urls = @(
            "https://eternallybored.org/misc/gifsicle/releases/gifsicle-1.95-win64.zip"
        )
        IsZip = $true
    },
    @{
        Name = "pngcrush"
        File = "pngcrush.exe"
        Urls = @(
            "https://pmt.sourceforge.io/pngcrush/pngcrush-1.8.13-win64.zip"
        )
        IsZip = $true
    },
    @{
        Name = "advpng"
        File = "advpng.exe"
        Urls = @(
            "https://github.com/amadvance/advancecomp/releases/download/v2.6/advancecomp-2.6-windows-x64.zip"
        )
        IsZip = $true
    },
    @{
        Name = "guetzli"
        File = "guetzli.exe"
        Urls = @(
            "https://github.com/google/guetzli/releases/download/v1.0.1/guetzli_windows_x86-64.exe"
        )
        IsZip = $false
    },
    @{
        Name = "svgcleaner"
        File = "svgcleaner.exe"
        Urls = @(
            "https://github.com/RazrFalcon/svgcleaner/releases/download/v0.9.5/svgcleaner_win32_0.9.5.zip"
        )
        IsZip = $true
    },
    @{
        Name = "svgo"
        File = "svgo.exe"
        Urls = @(
            "https://github.com/svg/svgo/releases/download/v3.3.2/svgo-win.exe"
        )
        IsZip = $false
    }
)

foreach ($tool in $tools) {
    $target = Join-Path $Destination $tool.File
    if (Test-Path $target) {
        Write-Host "[Skip] $($tool.Name) already exists: $target"
        continue
    }

    $downloaded = $false
    foreach ($url in $tool.Urls) {
        # Retry each URL up to 3 times to handle network jitter and large-file timeouts (e.g. the 50MB libjxl package)
        for ($attempt = 1; $attempt -le 3 -and -not $downloaded; $attempt++) {
            $tmpZip = Join-Path $env:TEMP "$($tool.Name)-download"
            try {
                if ($attempt -gt 1) {
                    Write-Host "[Retry] $($tool.Name) attempt $attempt <- $url"
                }
                else {
                    Write-Host "[Download] $($tool.Name) <- $url"
                }
                Invoke-WebRequest -Uri $url -OutFile $tmpZip -UseBasicParsing -TimeoutSec 300

                if ($tool.IsZip) {
                    # Extract and locate the exe
                    $extractDir = Join-Path $env:TEMP "$($tool.Name)-extract"
                    New-Item -ItemType Directory -Force -Path $extractDir | Out-Null
                    # Clear the extract dir first to avoid leftover exe from a previous tool affecting the match
                    Get-ChildItem -Path $extractDir -Recurse -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
                    Expand-Archive -Path $tmpZip -DestinationPath $extractDir -Force
                    # Prefer exact exe name inside the zip when ZipExeName is specified (avoid shared/ vs static/ confusion)
                    if ($tool.ContainsKey('ZipExeName') -and -not [string]::IsNullOrEmpty($tool.ZipExeName)) {
                        $exe = Get-ChildItem -Path $extractDir -Recurse -Filter $tool.ZipExeName | Select-Object -First 1
                    }
                    else {
                        $exe = Get-ChildItem -Path $extractDir -Recurse -Filter "*.exe" | Where-Object { $_.Name -like "*$($tool.Name)*" } | Select-Object -First 1
                    }
                    if ($exe) {
                        Copy-Item -Path $exe.FullName -Destination $target -Force
                        Write-Host "[Done] $($tool.Name) -> $target"
                        $downloaded = $true
                    }
                }
                else {
                    Copy-Item -Path $tmpZip -Destination $target -Force
                    Write-Host "[Done] $($tool.Name) -> $target"
                    $downloaded = $true
                }
            }
            catch {
                Write-Warning "[Failed] $($tool.Name) download failed (attempt $attempt) from $url: $($_.Exception.Message)"
                if ($attempt -lt 3) { Start-Sleep -Seconds 3 }
            }
            finally {
                Remove-Item -Path $tmpZip -Force -ErrorAction SilentlyContinue
            }
        }
        if ($downloaded) { break }
    }

    if (-not $downloaded) {
        Write-Warning "[Warning] Could not download $($tool.Name); please manually place $($tool.File) into $Destination"
    }
}

# Required tools: missing any of these breaks core PNG/JPEG optimization, so fail the build.
# Other tools (zopflipng/pngcrush/svgo, etc.) are optional enhancements; missing ones only warn.
$requiredTools = @(
    "oxipng.exe",    # core PNG lossless
    "pngquant.exe",  # core PNG lossy
    "jpegoptim.exe", # core JPEG lossless
    "cjpegli.exe",   # jpegli (preferred JPEG tool)
    "jpegtran.exe"   # MozJPEG (secondary JPEG tool)
)

$missingRequired = @()
foreach ($f in $requiredTools) {
    $p = Join-Path $Destination $f
    if (-not (Test-Path $p)) {
        $missingRequired += $f
    }
}

if ($missingRequired.Count -gt 0) {
    Write-Error "Missing required tools: $($missingRequired -join ', '). Please check the download URLs or manually place the exe files into $Destination"
    exit 1
}

Write-Host "All required tools are ready: $($requiredTools -join ', ')"
Write-Host "Tool download script completed."
