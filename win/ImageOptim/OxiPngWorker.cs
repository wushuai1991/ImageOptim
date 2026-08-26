namespace ImageOptim;

/// <summary>OxiPNG：PNG 无损优化工具。</summary>
public sealed class OxiPngWorker : Worker
{
    private readonly int _optlevel;
    private readonly bool _strip;

    public OxiPngWorker(Job job, int level, bool strip) : base(job)
    {
        _optlevel = Math.Max(2, Math.Min(level, 6));
        _strip = strip;
    }

    public override int SettingsIdentifier => 2 * (_optlevel * 2 + (_strip ? 1 : 0));

    public override bool OptimizeFile(ImageFile file, string tempPath)
    {
        var exe = FindExecutable("oxipng");
        if (exe == null) return false;

        var args = new List<string> { $"-o{_optlevel}", "-i0", "-a" };
        if (_strip)
            args.Insert(0, "--strip=safe");
        args.Add("--out");
        args.Add(tempPath);
        args.Add("--");
        args.Add(file.Path);

        int code = RunProcess(exe, args);
        if (code != 0)
            return false;

        return Job.SetFileOptimized(tempPath, "OxiPNG");
    }
}
