namespace ImageOptim;

/// <summary>Guetzli：JPEG 有损优化工具（极慢）。</summary>
public sealed class GuetzliWorker : Worker
{
    private readonly int _quality;

    public GuetzliWorker(Job job, Preferences prefs) : base(job)
    {
        _quality = prefs.LossyEnabled ? prefs.JpegOptimMaxQuality : 95;
        if (_quality < 84) _quality = 84;
    }

    public override int SettingsIdentifier => _quality;

    public override bool MakesNonOptimizingModifications => true;

    public override bool OptimizeFile(ImageFile file, string tempPath)
    {
        var exe = FindExecutable("guetzli");
        if (exe == null) return false;

        bool smallFile = file.IsSmall;
        var args = new List<string>
        {
            "--quality", _quality.ToString(),
            "--memlimit", smallFile ? "2000" : "6000",
            file.Path, tempPath,
        };

        // Guetzli 极慢且耗内存，使用较长超时
        int code = RunProcess(exe, args, timeoutMs: 30 * 60 * 1000);
        if (code != 0)
            return false;

        return Job.SetFileOptimized(tempPath, "Guetzli");
    }
}
