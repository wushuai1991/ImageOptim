using System.IO;

namespace ImageOptim;

/// <summary>pngquant：有损 PNG 量化工具。</summary>
public sealed class PngquantWorker : Worker
{
    private readonly int _minQuality;
    private readonly int _speed;

    public PngquantWorker(Job job, int level, int minQuality) : base(job)
    {
        _minQuality = minQuality;
        _speed = Math.Min(3, 7 - level);
    }

    public override int SettingsIdentifier => _minQuality;

    public override bool MakesNonOptimizingModifications => _minQuality < 100;

    public override bool OptimizeFile(ImageFile file, string tempPath)
    {
        var exe = FindExecutable("pngquant");
        if (exe == null) return false;

        int maxQuality = Math.Min(100, _minQuality + 20);
        var args = new List<string>
        {
            "256", "--skip-if-larger", $"-s{_speed}",
            "--quality", $"{_minQuality}-{maxQuality}",
            "-f", "--output", tempPath, "--", file.Path,
        };

        // pngquant 直接使用 --output 参数输出到文件，无需通过 stdin
        int code = RunProcess(exe, args);
        if (code != 0 && code != 98 && code != 99)
            return false;

        return Job.SetFileOptimized(tempPath, "pngquant");
    }
}
