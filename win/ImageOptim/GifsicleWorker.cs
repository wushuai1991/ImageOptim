namespace ImageOptim;

/// <summary>Gifsicle：GIF 优化工具。</summary>
public sealed class GifsicleWorker : Worker
{
    private readonly bool _interlace;
    private readonly int _quality;

    public GifsicleWorker(Job job, bool interlace, int quality) : base(job)
    {
        _interlace = interlace;
        _quality = quality;
    }

    public override int SettingsIdentifier => (_interlace ? 1 : 0) + 2 * _quality;

    public override bool OptimizeFile(ImageFile file, string tempPath)
    {
        var exe = FindExecutable("gifsicle");
        if (exe == null) return false;

        var args = new List<string>
        {
            "-o", tempPath,
            _interlace ? "--interlace" : "--no-interlace",
            "-O3", "--careful",
            "--no-comments", "--no-names", "--same-delay", "--same-loopcount", "--no-warnings",
        };

        bool isLossy = _quality < 100;
        if (isLossy)
        {
            double loss = Math.Pow(100 - _quality, 1.8) / 5.0;
            if (file.IsSmall)
                loss = 1 + loss / 8;
            else if (!file.IsLarge)
                loss = 1 + loss / 2;
            args.Insert(0, $"--lossy={(int)loss}");
        }

        args.Add("--");
        args.Add(file.Path);

        int code = RunProcess(exe, args);
        if (code != 0)
            return false;

        if (!File.Exists(tempPath))
            return false;

        string toolName = isLossy ? "Giflossy" : (_interlace ? "Gifsicle interlaced" : "Gifsicle");

        if (isLossy)
        {
            long outSize = new FileInfo(tempPath).Length;
            bool significantlySmaller = outSize * (105 + (100 - _quality) / 2) / 100 < file.ByteSize;
            if (!significantlySmaller)
                return false;
        }

        return Job.SetFileOptimized(tempPath, toolName);
    }
}
