using System.IO;

namespace ImageOptim;

/// <summary>pngcrush：PNG 无损优化工具。</summary>
public sealed class PngCrushWorker : Worker
{
    private readonly bool _strip;
    private readonly bool _brute;

    public PngCrushWorker(Job job, int level, Preferences prefs) : base(job)
    {
        _strip = prefs.PngOutRemoveChunks;
        _brute = level >= 6;
    }

    public override int SettingsIdentifier => _strip ? 1 : 0;

    public override bool MakesNonOptimizingModifications => _strip;

    public override bool OptimizeFile(ImageFile file, string tempPath)
    {
        var exe = FindExecutable("pngcrush");
        if (exe == null) return false;

        var args = new List<string> { "-nofilecheck", "-bail", "-blacken", "-reduce", "-cc" };
        if (_strip)
        {
            args.Insert(0, "-rem");
            args.Insert(1, "alla");
        }
        if (file.IsSmall || (_brute && !file.IsLarge))
            args.Insert(0, "-brute");

        args.Add("--");
        args.Add(file.Path);
        args.Add(tempPath);

        int code = RunProcess(exe, args);
        if (code != 0)
            return false;

        if (!File.Exists(tempPath) || new FileInfo(tempPath).Length <= 70)
            return false;

        return Job.SetFileOptimized(tempPath, "Pngcrush");
    }
}
