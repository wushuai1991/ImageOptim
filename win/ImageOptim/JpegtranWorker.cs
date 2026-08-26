namespace ImageOptim;

/// <summary>MozJPEG（jpegtran）：JPEG 无损优化工具。</summary>
public sealed class JpegtranWorker : Worker
{
    private readonly bool _strip;

    public JpegtranWorker(Job job, Preferences prefs) : base(job)
    {
        _strip = prefs.JpegTranStripAll;
    }

    public override int SettingsIdentifier => _strip ? 1 : 0;

    public override bool OptimizeFile(ImageFile file, string tempPath)
    {
        var exe = FindExecutable("jpegtran");
        if (exe == null) return false;

        var args = new List<string>
        {
            "-optimize",
            "-copy", _strip ? "none" : "all",
            "-outfile", tempPath,
            file.Path,
        };

        int code = RunProcess(exe, args);
        if (code != 0)
            return false;

        return Job.SetFileOptimized(tempPath, "MozJPEG");
    }
}
