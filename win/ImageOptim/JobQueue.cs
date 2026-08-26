using System.IO;

namespace ImageOptim;

/// <summary>
/// 任务队列：限制并发优化文件数与目录扫描数，并追踪整体忙碌状态。
/// 对应 macOS 原版的 <c>JobQueue</c>。
/// </summary>
public sealed class JobQueue
{
    private readonly SemaphoreSlim _fileSemaphore;
    private readonly SemaphoreSlim _dirSemaphore;
    private readonly Preferences _prefs;
    private readonly ResultsCache _cache;

    private readonly object _lock = new();
    private int _activeCount;
    private bool _isBusy;

    public event Action? BusyStateChanged;
    public event Action? QueueFinished;

    public bool IsBusy { get { lock (_lock) return _isBusy; } }

    public JobQueue(Preferences prefs, ResultsCache cache)
    {
        _prefs = prefs;
        _cache = cache;
        _fileSemaphore = new SemaphoreSlim(Math.Max(1, prefs.RunConcurrentFiles));
        _dirSemaphore = new SemaphoreSlim(Math.Max(1, prefs.RunConcurrentDirscans));
    }

    /// <summary>添加一个文件优化任务。</summary>
    public Job AddFile(string path)
    {
        var job = new Job(path, _cache, _prefs);
        job.StateChanged += OnJobStateChanged;
        Requeue(job);
        return job;
    }

    /// <summary>将已存在的 Job 重新入队执行（用于「重新优化」）。</summary>
    public void Requeue(Job job)
    {
        Task.Run(async () =>
        {
            BeginActive();
            await _fileSemaphore.WaitAsync();
            try
            {
                job.Start();
            }
            finally
            {
                _fileSemaphore.Release();
                EndActive();
            }
        });
    }

    /// <summary>添加一个目录扫描任务。</summary>
    public void AddDirectory(string path, Action<IReadOnlyList<string>> onFilesFound)
    {
        Task.Run(async () =>
        {
            BeginActive();
            await _dirSemaphore.WaitAsync();
            try
            {
                var files = ScanDirectory(path);
                onFilesFound(files);
            }
            finally
            {
                _dirSemaphore.Release();
                EndActive();
            }
        });
    }

    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", "node_modules", "bin", "obj", ".vs", ".idea",
        "Tools", "build", "dist"
    };

    private List<string> ScanDirectory(string path)
    {
        var extensions = _prefs.EnabledExtensions();
        var result = new List<string>();
        try
        {
            ScanDirectoryRecursive(path, extensions, result);
        }
        catch
        {
            // 忽略扫描异常
        }
        return result;
    }

    private static void ScanDirectoryRecursive(string path, HashSet<string> extensions, List<string> result)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly))
        {
            var ext = System.IO.Path.GetExtension(file).TrimStart('.');
            if (extensions.Contains(ext))
                result.Add(file);
        }

        foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly))
        {
            var name = System.IO.Path.GetFileName(dir);
            if (SkipDirectories.Contains(name))
                continue;
            try
            {
                ScanDirectoryRecursive(dir, extensions, result);
            }
            catch
            {
                // 忽略单个子目录访问异常
            }
        }
    }

    private void OnJobStateChanged()
    {
        // Job 完成时不再影响队列忙碌计数（由 Begin/End 维护）
    }

    private void BeginActive()
    {
        bool shouldNotify;
        lock (_lock)
        {
            _activeCount++;
            shouldNotify = !_isBusy;
            if (shouldNotify)
                _isBusy = true;
        }
        if (shouldNotify)
            BusyStateChanged?.Invoke();
    }

    private void EndActive()
    {
        bool shouldNotify;
        lock (_lock)
        {
            _activeCount--;
            shouldNotify = _activeCount <= 0;
            if (shouldNotify)
                _isBusy = false;
        }
        if (shouldNotify)
        {
            BusyStateChanged?.Invoke();
            QueueFinished?.Invoke();
        }
    }
}