using System.IO;

namespace ImageOptim;

/// <summary>
/// 表示一个待优化或已优化的图片文件。
/// 与 macOS 原版的 <c>File</c> 类对应，封装文件类型、字节大小与路径。
/// </summary>
public sealed class ImageFile
{
    public string Path { get; }
    public long ByteSize { get; }
    public FileType Type { get; }

    public ImageFile(string path, long byteSize, FileType type)
    {
        Path = path;
        ByteSize = byteSize;
        Type = type;
    }

    /// <summary>
    /// 读取文件头魔数，检测文件类型。
    /// </summary>
    public static ImageFile FromPath(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
            return new ImageFile(path, 0, FileType.Unknown);

        // WebP 需要看 RIFF(0-3) 和 WEBP(8-11) 两段，所以头要从 6 字节扩到 12
        var header = new byte[12];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            int read = fs.Read(header, 0, header.Length);
            if (read < header.Length)
                return new ImageFile(path, info.Length, FileType.Unknown);
        }

        var type = DetectType(header, path);
        return new ImageFile(path, info.Length, type);
    }

    /// <summary>根据文件头魔数判断类型。</summary>
    private static FileType DetectType(byte[] h, string path)
    {
        // PNG: 89 50 4E 47 0D 0A
        if (h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47 && h[4] == 0x0D && h[5] == 0x0A)
            return FileType.Png;

        // JPEG: FF D8 FF
        if (h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF)
            return FileType.Jpeg;

        // GIF: 47 49 46 38 ("GIF8")
        if (h[0] == 0x47 && h[1] == 0x49 && h[2] == 0x46 && h[3] == 0x38)
            return FileType.Gif;

        // SVG: '<' 's' 'v' 'g'
        if (h[0] == '<' && h[1] == 's' && h[2] == 'v' && h[3] == 'g')
            return FileType.Svg;

        // BMP: "BM"
        if (h[0] == 'B' && h[1] == 'M')
            return FileType.Bmp;

        // WebP: "RIFF" .... "WEBP"
        if (h[0] == 'R' && h[1] == 'I' && h[2] == 'F' && h[3] == 'F' &&
            h[8] == 'W' && h[9] == 'E' && h[10] == 'B' && h[11] == 'P')
            return FileType.Webp;

        // 后缀兜底判断 SVG
        if (string.Equals(System.IO.Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase))
            return FileType.Svg;

        return FileType.Unknown;
    }

    /// <summary>PNG 超过 250KB 视为大文件，其余超过 1MB 视为大文件。</summary>
    public bool IsLarge => Type == FileType.Png ? ByteSize > 250 * 1024 : ByteSize > 1024 * 1024;

    /// <summary>PNG 小于 2KB 视为小文件，其余小于 10KB 视为小文件。</summary>
    public bool IsSmall => Type == FileType.Png ? ByteSize < 2048 : ByteSize < 10 * 1024;

    public string? MimeType => Type switch
    {
        FileType.Png => "image/png",
        FileType.Jpeg => "image/jpeg",
        FileType.Gif => "image/gif",
        FileType.Svg => "image/svg",
        FileType.Bmp => "image/bmp",
        FileType.Webp => "image/webp",
        _ => null,
    };

    /// <summary>
    /// 判断 WebP 是否为动画（VP8X 的 animation 标志位，或存在 ANIM/ANMF chunk）。
    /// 动画 WebP 无法转成静态 JPEG，转换前需要显式拦住。
    /// </summary>
    public static bool IsAnimatedWebP(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var head = new byte[12];
            if (fs.Read(head, 0, head.Length) < head.Length)
                return false;
            if (head[0] != 'R' || head[1] != 'I' || head[2] != 'F' || head[3] != 'F')
                return false;
            if (head[8] != 'W' || head[9] != 'E' || head[10] != 'B' || head[11] != 'P')
                return false;

            // RIFF chunk 都是 8 字节头 + 载荷，逐个走；只扫文件头部 64KB，避免为判断读整个文件
            var body = new byte[64 * 1024];
            int length = fs.Read(body, 0, body.Length);
            int offset = 0;

            while (offset + 8 <= length)
            {
                if (Matches(body, offset, "ANIM") || Matches(body, offset, "ANMF"))
                    return true;

                // VP8X 的首字节是标志位，bit1 = animation
                if (Matches(body, offset, "VP8X") && offset + 8 < length && (body[offset + 8] & 0x02) != 0)
                    return true;

                uint chunkSize = (uint)body[offset + 4]
                               | ((uint)body[offset + 5] << 8)
                               | ((uint)body[offset + 6] << 16)
                               | ((uint)body[offset + 7] << 24);
                long advance = 8 + (long)chunkSize + (chunkSize & 1);
                if (advance <= 8)
                    break;
                offset += (int)advance;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool Matches(byte[] buffer, int offset, string ascii)
    {
        for (int i = 0; i < ascii.Length; i++)
        {
            if (buffer[offset + i] != (byte)ascii[i])
                return false;
        }
        return true;
    }
}
