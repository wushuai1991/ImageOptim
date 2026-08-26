namespace ImageOptim;

/// <summary>jpegoptim：JPEG 优化工具。</summary>
public sealed class JpegoptimWorker : Worker
{
    private readonly bool _strip;
    private readonly int _maxQuality;

    public JpegoptimWorker(Job job, Preferences prefs) : base(job)
    {
        _strip = prefs.JpegTranStripAll;
        _maxQuality = prefs.LossyEnabled ? prefs.JpegOptimMaxQuality : 100;
    }

    public override int SettingsIdentifier => _maxQuality * 2 + (_strip ? 1 : 0);

    public override bool MakesNonOptimizingModifications => _maxQuality < 100;

    public override bool OptimizeFile(ImageFile file, string tempPath)
    {
        var exe = FindExecutable("jpegoptim");
        if (exe == null) return false;

        File.Copy(file.Path, tempPath, overwrite: true);

        bool lossy = _maxQuality > 10 && _maxQuality < 100;
        var args = new List<string>
        {
            _strip ? "--strip-all" : "--strip-none",
            lossy ? "--all-progressive" : "--all-normal",
            "-v", "--", tempPath,
        };
        if (lossy)
            args.Insert(0, $"-m{_maxQuality}");

        long optimizedSize = 0;
        int code = RunProcess(exe, args, onOutput: output =>
        {
            // 解析 " --> 12345 bytes" 中的输出大小
            var idx = output.IndexOf(" --> ", StringComparison.Ordinal);
            if (idx >= 0)
            {
                var rest = output[(idx + 5)..];
                var num = new string(rest.TakeWhile(char.IsDigit).ToArray());
                if (long.TryParse(num, out var s))
                    optimizedSize = s;
            }
        });

        if (code != 0)
            return false;

        bool significantlySmaller = file.ByteSize * 0.95 > optimizedSize;
        if (!MakesNonOptimizingModifications || significantlySmaller)
        {
            return Job.SetFileOptimized(tempPath, lossy ? $"JpegOptim {_maxQuality}%" : "JpegOptim");
        }
        return false;
    }
}
