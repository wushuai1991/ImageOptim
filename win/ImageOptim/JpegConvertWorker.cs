using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageOptim;

/// <summary>
/// 把 PNG / BMP / WebP 转成 JPEG（WIC 编码，进程内完成）。
/// WebP 在系统缺少编解码器时（Win10 未装「WebP Image Extensions」）退到 Tools\dwebp.exe。
/// 与 macOS 的 JpegConvertWorker 对应：产物类型是 JPEG，
/// Job 保存时会因为「产物类型 ≠ 原图类型」改走「另存 .jpg」分支。
/// </summary>
public sealed class JpegConvertWorker : Worker
{
    private readonly int _quality;

    public JpegConvertWorker(Job job, Preferences prefs) : base(job)
    {
        // 与 macOS 版一致：复用「JPEG 质量」设置，不额外引入一个质量参数
        _quality = Math.Clamp(prefs.JpegOptimMaxQuality, 1, 100);
    }

    public override int SettingsIdentifier => 9000 + _quality;

    /// <summary>转换不是幂等优化：不能因为产物体积与上次相同就跳过。</summary>
    public override bool IsIdempotent => false;

    public override bool OptimizeFile(ImageFile file, string tempPath)
    {
        if (Job.Cancellation.IsCancellationRequested)
            return false;

        // 动画 WebP 转成静态 JPEG 会静默丢掉动效，直接跳过并说明原因
        if (file.Type == FileType.Webp && ImageFile.IsAnimatedWebP(file.Path))
        {
            Job.SetError("动画 WebP 不支持转换（会丢失动效）");
            return false;
        }

        var frame = DecodeFrame(file.Path);
        if (frame == null)
        {
            // 系统 WIC 没有 WebP 编解码器时退到 dwebp
            var decoded = DecodeWithDwebp(file.Path);
            if (decoded == null)
            {
                Job.SetError("当前系统无法解码该 WebP（缺少系统解码器，且未安装 dwebp）");
                return false;
            }

            try
            {
                frame = DecodeFrame(decoded);
            }
            finally
            {
                TryDelete(decoded);
            }
        }

        if (frame == null)
            return false;

        if (!EncodeJpeg(frame, tempPath))
            return false;

        if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
            return false;

        var toolName = file.Type switch
        {
            FileType.Bmp => "BMP→JPEG",
            FileType.Webp => "WebP→JPEG",
            _ => "PNG→JPEG",
        };
        return Job.SetFileOptimized(tempPath, toolName, FileType.Jpeg);
    }

    private static BitmapFrame? DecodeFrame(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return decoder.Frames.Count > 0 ? decoder.Frames[0] : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"解码 {path} 失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>JPEG 没有透明通道：先铺白底再绘制，避免透明区域变成黑色。</summary>
    private bool EncodeJpeg(BitmapFrame frame, string tempPath)
    {
        int width = frame.PixelWidth;
        int height = frame.PixelHeight;
        if (width <= 0 || height <= 0)
            return false;

        double dpiX = frame.DpiX > 0 ? frame.DpiX : 96;
        double dpiY = frame.DpiY > 0 ? frame.DpiY : 96;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, width, height);
            dc.DrawRectangle(Brushes.White, null, rect);
            dc.DrawImage(frame, rect);
        }

        var bitmap = new RenderTargetBitmap(width, height, dpiX, dpiY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        var encoder = new JpegBitmapEncoder { QualityLevel = _quality };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(tempPath))
        {
            encoder.Save(output);
        }

        return true;
    }

    /// <summary>系统解码器不可用时的兜底：调 libwebp 的 dwebp 解成 PNG。</summary>
    private string? DecodeWithDwebp(string sourcePath)
    {
        var exe = FindExecutable("dwebp");
        if (exe == null)
        {
            Debug.WriteLine("系统缺少 WebP 解码器，且 Tools 目录下没有 dwebp.exe");
            return null;
        }

        var output = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ImageOptim.dwebp.{Guid.NewGuid():N}.png");
        int code = RunProcess(exe, new[] { sourcePath, "-o", output });
        if (code != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
        {
            Debug.WriteLine($"dwebp 失败（exit={code}）: {sourcePath}");
            TryDelete(output);
            return null;
        }

        return output;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 临时文件清理失败不影响结果
        }
    }
}
