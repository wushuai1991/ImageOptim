# ImageOptim Windows tool download script.
# Called by MSBuild during build to download Windows prebuilt binaries of each
# image optimizer tool into the Tools directory.
# Download failures only produce warnings and do not block the build
# (users can manually place .exe files into the Tools directory).
#
# IMPORTANT: keep this file ASCII-only.
# Windows PowerShell 5.1 reads a BOM-less .ps1 as the system ANSI codepage, so any
# UTF-8 Chinese text gets mis-decoded, swallows nearby quotes and breaks parsing
# (see commit 5d5318a: "'<' is reserved for future use", "Missing closing '}'").
#
# ---------------------------------------------------------------------------
# Integrity policy
#   * Every artifact is pinned to a SHA-256 (the Sha256 field below) and verified
#     right after download, before extraction.
#   * A mismatch aborts the run with exit 1: it means the upstream artifact was
#     replaced/tampered with, or the version drifted.
#   * To upgrade a tool on purpose, run with -UpdateLock, copy the new hash into
#     this file, and let it be reviewed in the PR diff.
#   * Never use a "latest" URL; the URL must carry an explicit version.
# ---------------------------------------------------------------------------
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File download-tools.ps1 -Destination <ToolsDir>
#   powershell -NoProfile -ExecutionPolicy Bypass -File download-tools.ps1 -UpdateLock

param(
    [string]$Destination = "",
    [switch]$UpdateLock
)

$ErrorActionPreference = "Continue"

if ([string]::IsNullOrEmpty($Destination)) {
    $Destination = Join-Path $PSScriptRoot "..\ImageOptim\Tools"
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
Write-Host "Tool output directory: $Destination"

# Each tool entry: name, file name, download URL list (tried in priority order),
# Sha256 = expected hash of the downloaded artifact (the zip or the exe itself,
# NOT the exe extracted from a zip).
$tools = @(
    @{
        Name = "oxipng"
        File = "oxipng.exe"
        Urls = @(
            "https://github.com/oxipng/oxipng/releases/download/v9.1.4/oxipng-9.1.4-x86_64-pc-windows-msvc.zip"
        )
        IsZip = $true
        Sha256 = "304c300fa8ba7989f1b36da0298c7e887f9cedee0abf115290678a539e61d152"
    },
    @{
        # pngquant.org only serves the current release under a fixed file name, so the
        # version cannot be pinned in the URL. The Sha256 below is what actually freezes
        # the content: a new upstream release fails verification until reviewed.
        Name = "pngquant"
        File = "pngquant.exe"
        Urls = @(
            "https://pngquant.org/pngquant-windows.zip"
        )
        IsZip = $true
        Sha256 = "bd0257aeeccfe446a4cd764927e26f8af6051796f28abed104307284107b120d"
    },
    @{
        # Upstream is a raw/main file; the Sha256 below is what pins it.
        Name = "zopflipng"
        File = "zopflipng.exe"
        Urls = @(
            "https://github.com/imagemin/zopflipng-bin/raw/main/vendor/win32/x64/zopflipng.exe"
        )
        IsZip = $false
        Sha256 = "f9122c913b1ee21d9284428d535321893a8c78378ab32a62dfdba793ad65f7f8"
    },
    @{
        Name = "jpegoptim"
        File = "jpegoptim.exe"
        Urls = @(
            "https://github.com/tjko/jpegoptim/releases/download/v1.5.6/jpegoptim-1.5.6-x64-windows.zip"
        )
        IsZip = $true
        Sha256 = "db2d8caee88f2665b772c4591cba223b8b729500b88266dc06d4bf79d28fe11a"
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
        Sha256 = "8f53ebce91820c30c9fc9294f06380213c1e2e66b361718880580246b2be008e"
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
        Sha256 = "c8db69b2bbf9cff05447e60454b55317f719d9793b9100597bfcd4682836f7ee"
    },
    @{
        Name = "gifsicle"
        File = "gifsicle.exe"
        Urls = @(
            "https://eternallybored.org/misc/gifsicle/releases/gifsicle-1.95-win64.zip"
        )
        IsZip = $true
        Sha256 = "7e47dd0bfd5ee47f911464c57faeed89a8709a7625dd1c449b16579889539ee8"
    },
    @{
        Name = "advpng"
        File = "advpng.exe"
        Urls = @(
            "https://github.com/amadvance/advancecomp/releases/download/v2.6/advancecomp-2.6-windows-x64.zip"
        )
        IsZip = $true
        Sha256 = "39478e2810d2e6fecea321d11a8e8aad678e9e52f83c684d85dcb70342607704"
    },
    @{
        Name = "guetzli"
        File = "guetzli.exe"
        Urls = @(
            "https://github.com/google/guetzli/releases/download/v1.0.1/guetzli_windows_x86-64.exe"
        )
        IsZip = $false
        Sha256 = "bbac41d606c96cc57a94c802e581591647d6e1cfc83a97522509edbd392faeca"
    },
    @{
        Name = "svgcleaner"
        File = "svgcleaner.exe"
        Urls = @(
            "https://github.com/RazrFalcon/svgcleaner/releases/download/v0.9.5/svgcleaner_win32_0.9.5.zip"
        )
        IsZip = $true
        Sha256 = "97f2d832edbd7146261a01d0de3c64f7fdcb014fac384ec46597cb3fa9c77046"
    },
    @{
        # No usable Windows binary upstream (verified 2026-10-21):
        #   - pngcrush 1.8.13 win64 package on SourceForge returns 404;
        #   - svgo v3.3.2 only ships svgo.browser.js, there has never been a svgo-win.exe
        #     (it needs Node.js).
        # Both are optional tools, so mark them Unavailable and skip them instead of
        # burning 6 network retries on every build. Restore Urls + Sha256 once a working
        # Windows binary exists.
        Name = "pngcrush"
        File = "pngcrush.exe"
        Urls = @()
        IsZip = $true
        Unavailable = $true
    },
    @{
        Name = "svgo"
        File = "svgo.exe"
        Urls = @()
        IsZip = $false
        Unavailable = $true
    }
)

function Get-Sha256([string]$path) {
    return (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$integrityFailures = @()

foreach ($tool in $tools) {
    $target = Join-Path $Destination $tool.File
    if (Test-Path $target) {
        Write-Host "[Skip] $($tool.Name) already exists: $target"
        continue
    }

    if ($tool.ContainsKey('Unavailable') -and $tool.Unavailable) {
        Write-Host "[Skip] $($tool.Name) has no usable Windows binary upstream (optional tool)"
        continue
    }

    $downloaded = $false
    foreach ($url in $tool.Urls) {
        # Retry each URL up to 3 times to handle network jitter and large-file timeouts (e.g. the 50MB libjxl package)
        for ($attempt = 1; $attempt -le 3 -and -not $downloaded; $attempt++) {
            # Windows PowerShell 5.1 Expand-Archive requires the input path to end with .zip,
            # so zip tools must be downloaded to a .zip temp file, otherwise:
            # "is not a supported archive file format. .zip is the only supported archive file format."
            $tmpSuffix = if ($tool.IsZip) { ".zip" } else { ".bin" }
            $tmpZip = Join-Path $env:TEMP "$($tool.Name)-download$tmpSuffix"
            try {
                if ($attempt -gt 1) {
                    Write-Host "[Retry] $($tool.Name) attempt $attempt <- $url"
                }
                else {
                    Write-Host "[Download] $($tool.Name) <- $url"
                }
                Invoke-WebRequest -Uri $url -OutFile $tmpZip -UseBasicParsing -TimeoutSec 300

                # ---- Integrity check: verify the hash before extracting ----
                $actual = Get-Sha256 $tmpZip
                $expected = if ($tool.ContainsKey('Sha256')) { $tool.Sha256 } else { "" }

                if ([string]::IsNullOrEmpty($expected)) {
                    Write-Warning "[Unlocked] $($tool.Name) has no pinned SHA-256, actual: $actual"
                }
                elseif ($actual -ne $expected) {
                    if ($UpdateLock) {
                        # Intentional upgrade: report both hashes and keep going, the
                        # maintainer then copies the new value back into this file.
                        Write-Warning "[Update] $($tool.Name) hash changed (-UpdateLock mode, not fatal)"
                        Write-Host "          old: $expected"
                        Write-Host "          new: $actual"
                    }
                    else {
                        # A hash mismatch is a definitive verdict, retrying is pointless.
                        Write-Error "[Integrity] $($tool.Name) SHA-256 mismatch. expected: $expected actual: $actual source: $url For a deliberate upgrade run with -UpdateLock and update this script."
                        $integrityFailures += $tool.Name
                        break
                    }
                }
                else {
                    Write-Host "[Verified] $($tool.Name) sha256=$actual"
                }

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
                Write-Warning "[Failed] $($tool.Name) download failed (attempt $attempt) from ${url}: $($_.Exception.Message)"
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

# A hash mismatch is a supply-chain / integrity incident: fail the run.
# (MSBuild's ContinueOnError swallows the exit code, but the dedicated CI step and a
# local dotnet build both surface it.)
if ($integrityFailures.Count -gt 0) {
    Write-Error "Integrity check failed for: $($integrityFailures -join ', ')"
    exit 1
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
