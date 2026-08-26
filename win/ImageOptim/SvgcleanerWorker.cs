namespace ImageOptim;

/// <summary>Svgcleaner：SVG 优化工具。</summary>
public sealed class SvgcleanerWorker : Worker
{
    private readonly bool _lossy;

    public SvgcleanerWorker(Job job, bool lossy) : base(job)
    {
        _lossy = lossy;
    }

    public override int SettingsIdentifier => _lossy ? 5 : 6;

    public override bool OptimizeFile(ImageFile file, string tempPath)
    {
        var exe = FindExecutable("svgcleaner");
        if (exe == null) return false;

        var args = new List<string> { file.Path, tempPath };
        if (_lossy)
        {
            args.Insert(0, "--allow-bigger-file");
        }

        int code = RunProcess(exe, args);
        if (code != 0)
            return false;

        return Job.SetFileOptimized(tempPath, "Svgcleaner");
    }
}
