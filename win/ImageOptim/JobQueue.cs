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

    // 全局外部进程预算：整个应用同一时刻最多跑这么多外部优化进程。
    // 单个文件内的多个工具可以并行，但共享这份额度，所以进程总数不会爆炸。
    private readonly SemaphoreSlim _processBudget;

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
        // 与文件并发数同口径：单文件时能吃满这些额度并行跑工具，
        // 多文件时总额度不变，进程数上限与旧实现（文件并发 × 1 个工具）一致。
        _processBudget = new SemaphoreSlim(Math.Max(1, prefs.RunConcurrentFiles));
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
            // 等信号量是异步的，不占线程
            await _fileSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                // job.Start() 是长时间同步阻塞（内部会跑多个外部进程，彼此并行但受
                // _processBudget 约束）。直接在当前线程池线程上跑，并发数一调大就会把池子占满、
                // 饿死其他排队任务；用 LongRunning 让它跑在专用线程上。
                var budget = _processBudget;
                await Task.Factory.StartNew(() => job.Start(budget), CancellationToken.None,
                                            TaskCreationOptions.LongRunning, TaskScheduler.Default)
                          .ConfigureAwait(false);
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

    /// <summary>
    /// 一次扫描的结果。<paramref name="Truncated"/> 为 true 表示列表不可信
    /// （超过上限提前停止，或某个子目录访问异常），调用方不应复用它。
    /// </summary>
    public sealed record ScanResult(List<string> Files, bool Truncated);

    /// <summary>
    /// 扫描路径（文件或目录），最多收集 stopAt+1 个文件即可判断是否超阈值。
    /// 旧实现是"先完整统计一遍再正式扫一遍"，同一个目录被枚举两次；
    /// 现在把结果直接返回，未截断时调用方可直接复用，只有大目录才需要二次扫描。
    /// </summary>
    public ScanResult Scan(IEnumerable<string> paths, int stopAt)
    {
        var extensions = _prefs.EnabledExtensions();
        var result = new List<string>();
        bool truncated = false;

        foreach (var p in paths)
        {
            if (truncated)
                break;

            if (Directory.Exists(p))
            {
                try
                {
                    ScanDirectoryInto(p, extensions, result, stopAt, ref truncated);
                }
                catch
                {
                    // 目录不可访问：已有结果不完整，标记为不可复用
                    truncated = true;
                }
            }
            else if (File.Exists(p))
            {
                if (extensions.Contains(ExtensionOf(p)))
                {
                    result.Add(p);
                    if (result.Count > stopAt)
                        truncated = true;
                }
            }
        }

        return new ScanResult(result, truncated);
    }

    private static void ScanDirectoryInto(string path, HashSet<string> extensions, List<string> result, int stopAt, ref bool truncated)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly))
        {
            if (extensions.Contains(ExtensionOf(file)))
            {
                result.Add(file);
                if (result.Count > stopAt)
                {
                    truncated = true;
                    return;
                }
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly))
        {
            if (truncated)
                return;
            if (SkipDirectories.Contains(System.IO.Path.GetFileName(dir)))
                continue;
            try
            {
                ScanDirectoryInto(dir, extensions, result, stopAt, ref truncated);
            }
            catch
            {
                // 忽略单个子目录访问异常
                truncated = true;
            }
        }
    }

    private static string ExtensionOf(string path) => System.IO.Path.GetExtension(path).TrimStart('.');

    // 仅跳过版本控制与依赖目录（几乎不可能包含用户要优化的图片，且文件量可能极大）。
    // 不再跳过 build/dist/bin/obj/Tools 等目录，保证「文件夹中所有图片」语义。
    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", "node_modules"
    };

    private List<string> ScanDirectory(string path)
    {
        var extensions = _prefs.EnabledExtensions();
        var result = new List<string>();
        bool truncated = false;
        try
        {
            // 不限量（stopAt = MaxValue）复用同一套遍历逻辑，避免两份递归实现漂移
            ScanDirectoryInto(path, extensions, result, int.MaxValue, ref truncated);
        }
        catch
        {
            // 忽略扫描异常
        }
        return result;
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