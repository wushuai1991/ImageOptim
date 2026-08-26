namespace ImageOptim;

/// <summary>PNGOUT：PNG 无损优化工具。</summary>
public sealed class PngoutWorker : Worker
{
    private readonly int _level;
    private readonly bool _removeChunks;
    private readonly int _requestedLevel;

    public PngoutWorker(Job job, int level, Preferences prefs) : base(job)
    {
        _level = level == 0 ? 2 : (level >= 4 ? 0 : 1);
        _requestedLevel = level;
        _removeChunks = prefs.PngOutRemoveChunks;
    }

    public override int SettingsIdentifier => _level * 4 + (_removeChunks ? 2 : 0);

    public override bool MakesNonOptimizingModifications => _removeChunks;

    public override bool OptimizeFile(ImageFile file, string tempPath)
    {
        var exe = FindExecutable("pngout");
        if (exe == null) return false;

        int timeLimit = TimeLimitForLevel(_requestedLevel, file);

        int actualLevel = _level;
        if (file.IsLarge && _level < 2)
            actualLevel++;

        var args = new List<string> { "-r" };
        if (actualLevel > 0)
            args.Insert(0, $"-s{actualLevel}");
        if (!_removeChunks)
            args.Insert(0, "-k1");
        args.Add("-v");
        args.Add(file.Path);
        args.Add(tempPath);

        int code = RunProcess(exe, args, timeLimit * 1000);
        if (code != 0 && code != 2)
            return false;

        if (!File.Exists(tempPath))
            return false;

        return Job.SetFileOptimized(tempPath, "PNGOUT");
    }
}