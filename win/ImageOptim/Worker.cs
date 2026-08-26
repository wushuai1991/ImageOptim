using System.Diagnostics;

namespace ImageOptim;

/// <summary>
/// 优化工具基类。每个具体工具（pngquant、oxipng 等）继承此类，
/// 实现 <see cref="OptimizeFile"/> 来调用外部命令行二进制，产出临时优化结果。
/// 对应 macOS 原版的 <c>Worker</c> / <c>CommandWorker</c>。
/// </summary>
public abstract class Worker
{
    protected Job Job { get; }

    /// <summary>是否会对文件做非优化性的修改（如剥离元数据、有损压缩）。</summary>
    public virtual bool MakesNonOptimizingModifications => false;

    /// <summary>是否幂等（重复运行不会产生新结果）。</summary>
    public virtual bool IsIdempotent => true;

    /// <summary>参与设置哈希计算的标识，用于结果缓存失效判定。</summary>
    public abstract int SettingsIdentifier { get; }

    protected Worker(Job job)
    {
        Job = job;
    }

    /// <summary>
    /// 执行优化，将结果写入临时文件，并告知 Job 是否采纳更小结果。
    /// </summary>
    /// <param name="file">输入文件</param>
    /// <param name="tempPath">输出临时文件路径</param>
    /// <returns>是否成功产出并采纳结果</returns>
    public abstract bool OptimizeFile(ImageFile file, string tempPath);

    /// <summary>工具可执行文件所在目录（构建产物 Tools 目录）。</summary>
    protected static string ToolDirectory()
    {
        // 优先使用应用目录下的 Tools 子目录
        var appDir = AppContext.BaseDirectory;
        var tools = System.IO.Path.Combine(appDir, "Tools");
        if (Directory.Exists(tools))
            return tools;
        return appDir;
    }

    /// <summary>查找工具可执行文件路径（Windows 下优先 .exe）。</summary>
    protected string? FindExecutable(string name)
    {
        var dir = ToolDirectory();
        var candidates = new[]
        {
            System.IO.Path.Combine(dir, name + ".exe"),
            System.IO.Path.Combine(dir, name),
            System.IO.Path.Combine(dir, name + ".cmd"),
            System.IO.Path.Combine(dir, name + ".bat"),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c))
                return c;
        }
        return null;
    }

    /// <summary>生成唯一的临时文件路径。</summary>
    protected string NewTempPath()
    {
        return System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"ImageOptim.{GetType().Name}.{Environment.TickCount64}.{Guid.NewGuid():N}.tmp");
    }

    /// <summary>
    /// 运行外部进程，返回退出码；-1 表示启动失败。
    /// </summary>
    protected int RunProcess(string executable, IEnumerable<string> args, int timeoutMs = 0, Action<string>? onOutput = null, string? stdinData = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinData != null,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                return -1;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"启动失败 {executable}: {ex.Message}");
            return -1;
        }

        // 读取输出（异步避免阻塞管道）
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (stdinData != null)
        {
            process.StandardInput.Write(stdinData);
            process.StandardInput.Close();
        }

        if (timeoutMs > 0)
        {
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return -1;
            }
        }
        else
        {
            process.WaitForExit();
        }

        Task.WaitAll(stdout, stderr);
        var output = stdout.Result + "\n" + stderr.Result;
        onOutput?.Invoke(output);

        return process.ExitCode;
    }

    /// <summary>根据优化级别计算时间上限（秒），与 macOS 版 timelimitForLevel 一致。</summary>
    protected int TimeLimitForLevel(int level, ImageFile file)
    {
        var timelimit = 10 + file.ByteSize / 1024;
        var maxTime = 8 + level * 13;
        return (int)Math.Min(maxTime, timelimit);
    }
}
