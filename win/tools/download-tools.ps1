# ImageOptim Windows 版工具下载脚本
# 在构建时被 MSBuild 调用，下载各图片优化工具的 Windows 预编译二进制到 Tools 目录。
# 若下载失败，仅输出警告而不阻断构建（用户可手动将 .exe 放入 Tools 目录）。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File download-tools.ps1 -Destination <Tools目录>

param(
    [string]$Destination = ""
)

$ErrorActionPreference = "Continue"

if ([string]::IsNullOrEmpty($Destination)) {
    $Destination = Join-Path $PSScriptRoot "..\ImageOptim\Tools"
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
Write-Host "工具输出目录: $Destination"

# 每个工具条目：名称、文件名、下载 URL 列表（按优先级尝试）
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
        # jpegli（首选 JPEG 工具）：从libjxl 官方 Windows 静态包中提取 cjpegli.exe
        Name = "cjpegli"
        File = "cjpegli.exe"
        Urls = @(
            "https://github.com/libjxl/libjxl/releases/download/v0.11.1/jxl-x64-windows-static.zip"
        )
        IsZip = $true
        ZipExeName = "cjpegli.exe"
    },
    @{
        # MozJPEG 官方 Windows 静态版 jpegtran（次选）：从 mozjpeg-v4.0.3-win-x64.zip 中提取 static/Release/jpegtran-static.exe
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
        Write-Host "[跳过] $($tool.Name) 已存在: $target"
        continue
    }

    $downloaded = $false
    foreach ($url in $tool.Urls) {
        # 每个 URL 最多重试 3 次，应对网络抖动与大文件（如 50MB 的 libjxl 包）下载超时
        for ($attempt = 1; $attempt -le 3 -and -not $downloaded; $attempt++) {
            $tmpZip = Join-Path $env:TEMP "$($tool.Name)-download"
            try {
                if ($attempt -gt 1) {
                    Write-Host "[重试] $($tool.Name) 第 $attempt 次尝试 <- $url"
                }
                else {
                    Write-Host "[下载] $($tool.Name) <- $url"
                }
                Invoke-WebRequest -Uri $url -OutFile $tmpZip -UseBasicParsing -TimeoutSec 300

                if ($tool.IsZip) {
                    # 解压并查找 exe
                    $extractDir = Join-Path $env:TEMP "$($tool.Name)-extract"
                    New-Item -ItemType Directory -Force -Path $extractDir | Out-Null
                    # 解压前先清空，避免上一个工具的同名 exe 残留影响匹配
                    Get-ChildItem -Path $extractDir -Recurse -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
                    Expand-Archive -Path $tmpZip -DestinationPath $extractDir -Force
                    # 优先用显式指定的 zip 内 exe 名称精确匹配（避免 shared/ 与 static/ 混淆）
                    if ($tool.ContainsKey('ZipExeName') -and -not [string]::IsNullOrEmpty($tool.ZipExeName)) {
                        $exe = Get-ChildItem -Path $extractDir -Recurse -Filter $tool.ZipExeName | Select-Object -First 1
                    }
                    else {
                        $exe = Get-ChildItem -Path $extractDir -Recurse -Filter "*.exe" | Where-Object { $_.Name -like "*$($tool.Name)*" } | Select-Object -First 1
                    }
                    if ($exe) {
                        Copy-Item -Path $exe.FullName -Destination $target -Force
                        Write-Host "[完成] $($tool.Name) -> $target"
                        $downloaded = $true
                    }
                }
                else {
                    Copy-Item -Path $tmpZip -Destination $target -Force
                    Write-Host "[完成] $($tool.Name) -> $target"
                    $downloaded = $true
                }
            }
            catch {
                Write-Warning "[失败] $($tool.Name) 从 $url 下载失败(第 $attempt 次): $($_.Exception.Message)"
                if ($attempt -lt 3) { Start-Sleep -Seconds 3 }
            }
            finally {
                Remove-Item -Path $tmpZip -Force -ErrorAction SilentlyContinue
            }
        }
        if ($downloaded) { break }
    }

    if (-not $downloaded) {
        Write-Warning "[警告] 无法下载 $($tool.Name)，请手动将 $($tool.File) 放入 $Destination"
    }
}

# 必需工具：缺失会导致核心 PNG/JPEG 压缩能力不可用，必须全部就绪，否则以失败退出。
# 其余工具（zopflipng/pngcrush/svgo 等）为可选增强，缺失仅警告不阻断。
$requiredTools = @(
    "oxipng.exe",    # PNG 无损核心
    "pngquant.exe",  # PNG 有损核心
    "jpegoptim.exe", # JPEG 无损核心
    "cjpegli.exe",   # jpegli（首选 JPEG 工具）
    "jpegtran.exe"   # MozJPEG（次选 JPEG 工具）
)

$missingRequired = @()
foreach ($f in $requiredTools) {
    $p = Join-Path $Destination $f
    if (-not (Test-Path $p)) {
        $missingRequired += $f
    }
}

if ($missingRequired.Count -gt 0) {
    Write-Error "必需工具缺失: $($missingRequired -join ', ')。请检查下载链接或手动将对应 exe 放入 $Destination"
    exit 1
}

Write-Host "所有必需工具均已就绪: $($requiredTools -join ', ')"
Write-Host "工具下载脚本执行完毕。"
