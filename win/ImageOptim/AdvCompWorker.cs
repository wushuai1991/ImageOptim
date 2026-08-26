namespace ImageOptim;

/// <summary>AdvPNG：PNG 无损优化工具（AdvanceCOMP）。</summary>
public sealed class AdvCompWorker : Worker
{
    private readonly int _level;

    public AdvCompWorker(Job job, int level) : base(job)
    {
        _level = Math.Max(1, Math.Min(4, level));
    }

    public override int SettingsIdentifier => _level;

    public override bool OptimizeFile(ImageFile file, string tempPath)
    {
        var exe = FindExecutable("advpng");
        if (exe == null) return false;

        // advpng 原地修改，需先拷贝到临时文件
        File.Copy(file.Path, tempPath, overwrite: true);

        var args = new List<string> { $"-{_level}", "-z", "--", tempPath };
        int code = RunProcess(exe, args);
        if (code != 0)
            return false;

        return Job.SetFileOptimized(tempPath, "AdvPNG");
    }
}
