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
            "https://github.com/shssoichiro/oxipng/releases/latest/download/oxipng-9.1.4-x86_64-pc-windows-msvc.zip"
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
            "https://github.com/imagemin/jpegoptim-bin/raw/main/vendor/win32/x64/jpegoptim.exe"
        )
        IsZip = $false
    },
    @{
        Name = "jpegtran"
        File = "jpegtran.exe"
        Urls = @(
            "https://github.com/imagemin/mozjpeg-bin/raw/main/vendor/win32/x64/jpegtran.exe"
        )
        IsZip = $false
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
            "https://github.com/amadvance/advancecomp/releases/download/v2.6/advancecomp-2.6-win64.zip"
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
        $tmpZip = Join-Path $env:TEMP "$($tool.Name)-download"
        try {
            Write-Host "[下载] $($tool.Name) <- $url"
            Invoke-WebRequest -Uri $url -OutFile $tmpZip -UseBasicParsing -TimeoutSec 120

            if ($tool.IsZip) {
                # 解压并查找 exe
                $extractDir = Join-Path $env:TEMP "$($tool.Name)-extract"
                New-Item -ItemType Directory -Force -Path $extractDir | Out-Null
                Expand-Archive -Path $tmpZip -DestinationPath $extractDir -Force
                $exe = Get-ChildItem -Path $extractDir -Recurse -Filter "*.exe" | Where-Object { $_.Name -like "*$($tool.Name)*" } | Select-Object -First 1
                if ($exe) {
                    Copy-Item -Path $exe.FullName -Destination $target -Force
                    Write-Host "[完成] $($tool.Name) -> $target"
                    $downloaded = $true
                    break
                }
            }
            else {
                Copy-Item -Path $tmpZip -Destination $target -Force
                Write-Host "[完成] $($tool.Name) -> $target"
                $downloaded = $true
                break
            }
        }
        catch {
            Write-Warning "[失败] $($tool.Name) 从 $url 下载失败: $($_.Exception.Message)"
        }
        finally {
            Remove-Item -Path $tmpZip -Force -ErrorAction SilentlyContinue
        }
    }

    if (-not $downloaded) {
        Write-Warning "[警告] 无法下载 $($tool.Name)，请手动将 $($tool.File) 放入 $Destination"
    }
}

Write-Host "工具下载脚本执行完毕。"
