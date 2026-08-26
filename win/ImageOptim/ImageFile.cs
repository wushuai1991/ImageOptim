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

        var header = new byte[6];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            int read = fs.Read(header, 0, header.Length);
            if (read < 6)
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
        _ => null,
    };
}
