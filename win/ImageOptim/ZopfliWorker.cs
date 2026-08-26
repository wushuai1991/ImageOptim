namespace ImageOptim;

/// <summary>ZopfliPNG：PNG 无损优化工具。</summary>
public sealed class ZopfliWorker : Worker
{
    private readonly int _iterations;
    private readonly bool _strip;
    private readonly int _requestedLevel;
    public bool AlternativeStrategy { get; set; }

    public ZopfliWorker(Job job, int level, Preferences prefs) : base(job)
    {
        _iterations = 3 + 3 * level;
        _strip = prefs.PngOutRemoveChunks;
        _requestedLevel = level;
    }

    public override int SettingsIdentifier => _iterations * 4 + (_strip ? 2 : 0) + (AlternativeStrategy ? 1 : 0);

    public override bool MakesNonOptimizingModifications => true;

    public override bool IsIdempotent => false;

    public override bool OptimizeFile(ImageFile file, string tempPath)
    {
        var exe = FindExecutable("zopflipng");
        if (exe == null) return false;

        var args = new List<string> { "--lossy_transparent", "-y" };
        if (!_strip)
        {
            args.Insert(0, "--keepchunks=tEXt,zTXt,iTXt,gAMA,sRGB,iCCP,bKGD,pHYs,sBIT,tIME,oFFs,acTL,fcTL,fdAT");
        }

        int actualIterations = _iterations;
        string filters = "--filters=0pme";
        int timeLimit = TimeLimitForLevel(_requestedLevel, file);

        if (file.IsLarge)
        {
            actualIterations = 5 + actualIterations / 3;
            filters = "--filters=p";
        }
        if (AlternativeStrategy)
        {
            timeLimit = (int)(timeLimit * 1.4);
            filters = "--filters=bp";
        }
        else
        {
            timeLimit = (int)(timeLimit * 0.8);
        }

        args.Insert(0, filters);
        if (actualIterations > 0)
            args.Insert(0, $"--iterations={actualIterations}");
        args.Insert(0, $"--timelimit={timeLimit}");

        args.Add(file.Path);
        args.Add(tempPath);

        int code = RunProcess(exe, args, (timeLimit + 5) * 1000);
        if (code != 0)
            return false;

        if (!File.Exists(tempPath) || new FileInfo(tempPath).Length <= 70)
            return false;

        return Job.SetFileOptimized(tempPath, "Zopfli");
    }
}
