namespace ImageOptim;

/// <summary>SVGO：SVG 优化工具（通过 Node 运行脚本）。</summary>
public sealed class SvgoWorker : Worker
{
    private readonly bool _lossy;

    public SvgoWorker(Job job, bool lossy) : base(job)
    {
        _lossy = lossy;
    }

    public override int SettingsIdentifier => _lossy ? 5 : 6;

    public override bool OptimizeFile(ImageFile file, string tempPath)
    {
        var exe = FindExecutable("svgo");
        if (exe == null) return false;

        var args = new List<string>
        {
            "--input", file.Path,
            "--output", tempPath,
            "--multipass",
        };
        if (_lossy)
            args.Add("--pretty=false");

        int code = RunProcess(exe, args);
        if (code != 0)
            return false;

        return Job.SetFileOptimized(tempPath, _lossy ? "SVGO" : "SVGO lite");
    }
}
