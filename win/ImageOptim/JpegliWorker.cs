using System.IO;

namespace ImageOptim;

/// <summary>
/// jpegli：Google/libjxl 项目的现代 JPEG 编码器。
/// 在视觉质量下可比 MozJPEG 提供更高压缩率，作为 JPEG 优化的首选工具。
///
/// 命令行工具名：cjpegli.exe（来自 libjxl 官方 Windows 静态构建）
/// </summary>
public sealed class JpegliWorker : Worker
{
    private readonly int _quality;
    private readonly bool _strip;

    public JpegliWorker(Job job, Preferences prefs) : base(job)
    {
        // 与 JpegOptim 保持一致：启用有损时使用用户设定的最大质量；否则用较高质量（92）近视觉无损
        _quality = prefs.LossyEnabled ? prefs.JpegOptimMaxQuality : 92;
        _strip = prefs.JpegTranStripAll;
    }

    public override int SettingsIdentifier => _quality * 2 + (_strip ? 1 : 0);

    /// <summary>jpegli 重新编码 JPEG，属于视觉近无损但字节层面有变化。</summary>
    public override bool MakesNonOptimizingModifications => true;

    public override bool OptimizeFile(ImageFile file, string tempPath)
    {
        var exe = FindExecutable("cjpegli");
        if (exe == null) return false;

        // cjpegli 用法：cjpegli <input> <output> [-q <quality>] [--chroma_subsampling=<value>]
        // 参考：https://github.com/libjxl/libjxl/blob/main/tools/cjpegli.cc
        var args = new List<string>
        {
            file.Path,
            tempPath,
            "-q", _quality.ToString(),
        };

        // 高质量段（>=90）建议保持 4:4:4 子采样以最大化保真；低质量段允许默认 4:2:0
        if (_quality >= 90)
        {
            args.Add("--chroma_subsampling=444");
        }

        // jpegli 编码本身不含元数据处理开关；若 _strip=false，此处保留原始行为（保守：不额外处理元数据）
        // cjpegli 会按其内置策略处理 EXIF/ICC（默认保留 ICC）

        int code = RunProcess(exe, args);
        if (code != 0)
            return false;

        if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
            return false;

        return Job.SetFileOptimized(tempPath, "jpegli");
    }
}
