using System.Diagnostics;
using System.Security.Cryptography;

namespace ImageOptim;

/// <summary>
/// 单个文件优化任务的 UI 快照（不可变，线程安全发布）。
/// 对应 macOS 原版的 <c>JobProxy</c> 所代理的属性集合。
/// </summary>
public sealed record JobSnapshot(
    string FilePath,
    string DisplayName,
    string? BestToolName,
    string StatusImage,
    string StatusText,
    bool IsBusy,
    bool IsDone,
    bool IsFailed,
    bool IsOptimized,
    long? ByteSizeOriginal,
    long? ByteSizeOptimized,
    double? PercentOptimized,
    bool CanRevert);

/// <summary>
/// 单个文件的优化任务。核心逻辑与 macOS 原版 <c>Job.m</c> 一致：
/// 1. 根据文件类型选择一组优化工具；
/// 2. 各工具产出临时结果，持续比较并保留体积最小者；
/// 3. 全部完成后若结果更小则替换原文件，并保留可回退备份。
/// </summary>
public sealed class Job
{
    private readonly object _lock = new();

    public string FilePath { get; }
    public string DisplayName { get; }

    private ImageFile? _initialInput;
    private ImageFile? _unoptimizedInput;
    private ImageFile? _wipInput;
    private ImageFile? _savedOutput;
    private ImageFile? _revertFile;

    private string? _bestToolName;
    private string _statusImage = "wait";
    private string _statusText = "等待优化";
    private int _statusOrder;
    private bool _isDone;
    private bool _isFailed;
    private bool _stopping;

    private readonly List<Worker> _workers = new();
    private readonly Dictionary<string, (long fileSize, double ratio)> _bestTools = new();

    private volatile JobSnapshot _snapshot;
    private readonly ResultsCache _cache;
    private readonly Preferences _prefs;
    private bool _lossyConverted;

    public event Action? StateChanged;

    public JobSnapshot Snapshot => _snapshot;

    public Job(string filePath, ResultsCache cache, Preferences prefs)
    {
        FilePath = filePath;
        DisplayName = System.IO.Path.GetFileName(filePath);
        _cache = cache;
        _prefs = prefs;
        SetStatus("wait", 0, "等待优化");
    }

    private void Publish()
    {
        _snapshot = BuildSnapshot();
        StateChanged?.Invoke();
    }

    private JobSnapshot BuildSnapshot()
    {
        lock (_lock)
        {
            var optimizedFile = _wipInput;
            if (optimizedFile == null) optimizedFile = _savedOutput;
            var byteSizeOptimized = optimizedFile?.ByteSize;
            if (byteSizeOptimized == null)
            {
                optimizedFile = _unoptimizedInput;
                byteSizeOptimized = optimizedFile?.ByteSize;
            }

            var percent = ComputePercent();
            var isOptimized = IsOptimizedLocked();

            return new JobSnapshot(
                FilePath,
                DisplayName,
                _bestToolName,
                _statusImage,
                _statusText,
                IsBusyLocked(),
                _isDone,
                _isFailed,
                isOptimized,
                _initialInput?.ByteSize,
                byteSizeOptimized,
                percent,
                CanRevertLocked());
        }
    }

    private double? ComputePercent()
    {
        var optimizedFile = _wipInput;
        if (optimizedFile == _unoptimizedInput && _savedOutput == null)
            return null;

        optimizedFile = _wipInput ?? _savedOutput;
        if (optimizedFile == null && _isDone && !_isFailed)
            return 0;

        var byteSizeOptimized = (optimizedFile ?? _unoptimizedInput)?.ByteSize;
        var byteSizeOriginal = _initialInput?.ByteSize;
        if (byteSizeOptimized == null || byteSizeOriginal == null || byteSizeOriginal == 0)
            return null;

        double p = 100.0 - 100.0 * byteSizeOptimized.Value / byteSizeOriginal.Value;
        if (p < 0) return 0;
        return p;
    }

    private bool IsOptimizedLocked()
    {
        var optimizedFile = _wipInput ?? _savedOutput;
        if (optimizedFile == null || _unoptimizedInput == optimizedFile)
            return false;
        return optimizedFile.ByteSize < _unoptimizedInput.ByteSize;
    }

    private bool CanRevertLocked() => _revertFile != null && _isDone && !_stopping;

    private bool IsBusyLocked() => _workers.Count > 0;

    public bool IsBusy { get { lock (_lock) return IsBusyLocked(); } }

    /// <summary>启动优化：读取文件、构建工具链并执行（同步，由 JobQueue 负责并发调度）。</summary>
    public void Start()
    {
        RunAsync();
    }

    private void RunAsync()
    {
        try
        {
            SetStatus("progress", 3, "检查文件");

            var input = ImageFile.FromPath(FilePath);
            if (input.Type == FileType.Unknown || input.ByteSize == 0)
            {
                _initialInput = null;
                SetError("无法打开文件");
                return;
            }

            lock (_lock)
            {
                bool hasChangedSinceLastSave = _savedOutput != null && _savedOutput.ByteSize != input.ByteSize;
                bool hasBeenRunBefore = _initialInput != null && !hasChangedSinceLastSave;

                if (!hasBeenRunBefore || hasChangedSinceLastSave)
                {
                    _initialInput = input;
                    _unoptimizedInput = input;
                    _revertFile = null;
                    _savedOutput = null;
                    _bestToolName = null;
                    _lossyConverted = false;
                    _bestTools.Clear();
                }
                else
                {
                    _unoptimizedInput = input;
                }
                _wipInput = input;
            }

            // 构建工具链（基于当前设置与文件类型）
            var workers = BuildWorkers(input);
            if (workers.Count == 0)
            {
                lock (_lock) { _isDone = true; }
                SetError("所有必要的工具都已在偏好设置中禁用");
                Cleanup();
                return;
            }

            // 检查结果缓存（基于工具链的设置哈希）
            var settingsHash = ComputeSettingsHash(workers);
            var fileHash = ResultsCache.ComputeHash(settingsHash, FilePath);
            if (_cache.HasResult(fileHash))
            {
                SetStatus("noopt", 5, "文件无法进一步优化");
                lock (_lock) { _isDone = true; _wipInput = null; }
                Publish();
                return;
            }

            lock (_lock)
            {
                _isDone = false;
                _isFailed = false;
                _stopping = false;
                _workers.Clear();
                _workers.AddRange(workers);
            }

            // 依次运行各工具，每个产出结果立即比较采纳
            bool anySucceeded = false;
            foreach (var w in workers)
            {
                if (IsStopping()) break;

                var name = w.GetType().Name.Replace("Worker", "");
                SetStatus("progress", 4, $"开始 {name}");

                string tempPath = w.NewTempPath();
                try
                {
                    bool ok = w.OptimizeFile(input, tempPath);
                    if (ok)
                        anySucceeded = true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"{name} 失败: {ex.Message}");
                }
                finally
                {
                    TryDelete(tempPath);
                }
            }

            lock (_lock)
            {
                _workers.Clear();
            }

            if (IsStopping())
            {
                Publish();
                return;
            }

            SaveResultAndUpdateStatus(settingsHash, input);
        }
        catch (Exception ex)
        {
            SetError("内部错误：" + ex.Message);
        }
        finally
        {
            Publish();
        }
    }

    private byte[] ComputeSettingsHash(List<Worker> workers)
    {
        using var md5 = MD5.Create();
        using var ms = new MemoryStream();
        ms.WriteByte(3);
        foreach (var w in workers)
        {
            var id = BitConverter.GetBytes(w.SettingsIdentifier);
            ms.Write(id);
        }
        return md5.ComputeHash(ms.ToArray());
    }

    /// <summary>根据文件类型与偏好设置构建工具列表。</summary>
    private List<Worker> BuildWorkers(ImageFile input)
    {
        var list = new List<Worker>();
        int level = _prefs.AdvPngLevel;
        bool lossy = _prefs.LossyEnabled;

        switch (input.Type)
        {
            case FileType.Png:
                if (lossy && !_lossyConverted && _prefs.PngMinQuality < 100 && _prefs.PngMinQuality > 30)
                {
                    list.Add(new PngquantWorker(this, level, _prefs.PngMinQuality));
                    _lossyConverted = true;
                }
                if (_prefs.PngCrush2Enabled) list.Add(new PngCrushWorker(this, level, _prefs));
                if (_prefs.OptiPngEnabled) list.Add(new OxiPngWorker(this, level, _prefs.PngOutRemoveChunks));
                if (_prefs.PngOutEnabled) list.Add(new PngoutWorker(this, level, _prefs));
                if (_prefs.AdvPngEnabled && _prefs.PngOutRemoveChunks) list.Add(new AdvCompWorker(this, level));
                if (_prefs.ZopfliEnabled) list.Add(new ZopfliWorker(this, level, _prefs) { AlternativeStrategy = false });
                break;

            case FileType.Jpeg:
                if (!_lossyConverted && _prefs.GuetzliEnabled && _prefs.JpegOptimMaxQuality >= 80)
                {
                    list.Add(new GuetzliWorker(this, _prefs));
                    _lossyConverted = true;
                }
                if (_prefs.JpegOptimEnabled) list.Add(new JpegoptimWorker(this, _prefs));
                if (_prefs.JpegTranEnabled) list.Add(new JpegtranWorker(this, _prefs));
                break;

            case FileType.Gif:
                if (_prefs.GifsicleEnabled)
                {
                    if (lossy && !_lossyConverted && _prefs.GifQuality < 100 && _prefs.GifQuality > 30)
                    {
                        list.Add(new GifsicleWorker(this, interlace: false, _prefs.GifQuality));
                        _lossyConverted = true;
                    }
                    else
                    {
                        list.Add(new GifsicleWorker(this, interlace: false, 100));
                        if (level > 1)
                            list.Add(new GifsicleWorker(this, interlace: true, 100));
                    }
                }
                break;

            case FileType.Svg:
                if (_prefs.SvgoEnabled) list.Add(new SvgoWorker(this, lossy));
                if (_prefs.SvgcleanerEnabled) list.Add(new SvgcleanerWorker(this, lossy));
                break;

            default:
                break;
        }

        return list;
    }

    /// <summary>比较并采纳工具产出的更小结果。</summary>
    public bool SetFileOptimized(string tempPath, string toolName)
    {
        if (!File.Exists(tempPath))
            return false;

        long newSize = new FileInfo(tempPath).Length;
        if (newSize == 0)
            return false;

        lock (_lock)
        {
            var oldFile = _wipInput;
            if (oldFile == null)
                return false;
            long oldSize = oldFile.ByteSize;

            bool isSmaller = newSize < oldSize;
            Debug.WriteLine($"{toolName} {(isSmaller ? "优化了" : "未优化")} 文件: {oldSize} -> {newSize}");

            if (!isSmaller)
                return false;

            var newFile = new ImageFile(tempPath, newSize, oldFile.Type);
            _wipInput = newFile;

            _bestTools[toolName] = (newSize, (double)oldSize / newSize);
            UpdateBestToolName();
        }
        Publish();
        return true;
    }

    private void UpdateBestToolName()
    {
        string? smallestTool = null;
        long smallestSize = _unoptimizedInput?.ByteSize ?? long.MaxValue;
        string? bestRatioTool = null;
        double bestRatio = 0;

        foreach (var (name, (fileSize, ratio)) in _bestTools)
        {
            if (ratio > bestRatio) { bestRatioTool = name; bestRatio = ratio; }
            if (fileSize < smallestSize) { smallestTool = name; smallestSize = fileSize; }
        }

        string? newBest;
        if (smallestTool != null && bestRatioTool != null && smallestTool != bestRatioTool)
            newBest = $"{bestRatioTool}+{smallestTool}";
        else
            newBest = smallestTool ?? bestRatioTool;

        if (newBest != _bestToolName)
            _bestToolName = newBest;
    }

    private void SaveResultAndUpdateStatus(byte[] settingsHash, ImageFile input)
    {
        lock (_lock)
        {
            if (IsOptimizedLocked())
            {
                bool saved = SaveResultLocked();
                _isDone = true;
                _workers.Clear();
                if (saved)
                    SetStatus("ok", 7, $"已用 {_bestToolName} 成功优化");
                else
                    SetError("优化后的文件无法保存");
            }
            else
            {
                _wipInput = null;
                SetStatus("noopt", 5, "文件无法进一步优化");
                _isDone = true;
                _workers.Clear();
                if (!_stopping && !_isFailed)
                {
                    var fileHash = ResultsCache.ComputeHash(settingsHash, FilePath);
                    _cache.MarkUnoptimizable(fileHash);
                }
            }
        }
    }

    /// <summary>用临时优化结果替换原文件，并保留回退备份。</summary>
    private bool SaveResultLocked()
    {
        var fileToSave = _wipInput;
        if (fileToSave == null)
            return false;

        try
        {
            var dir = System.IO.Path.GetDirectoryName(FilePath)!;
            var original = _unoptimizedInput!;

            // 备份原文件（保留创建/修改时间与属性）
            string backupPath = System.IO.Path.Combine(dir, "~" + System.IO.Path.GetFileNameWithoutExtension(FilePath) + ".imageoptim.bak" + System.IO.Path.GetExtension(FilePath));
            if (File.Exists(backupPath))
                File.Delete(backupPath);

            File.Copy(FilePath, backupPath, overwrite: true);
            if (!_revertFileObjExists())
                _revertFile = new ImageFile(backupPath, original.ByteSize, original.Type);

            // 保留原文件时间
            var creation = File.GetCreationTime(FilePath);
            var modified = File.GetLastWriteTime(FilePath);

            // 用优化结果覆盖原文件
            File.Copy(fileToSave.Path, FilePath, overwrite: true);

            if (_prefs.PreserveDates)
            {
                File.SetCreationTime(FilePath, creation);
                File.SetLastWriteTime(FilePath, modified);
            }

            if (!_prefs.PreservePermissions)
            {
                // 默认新文件已继承，无需处理；保留则维持原属性
            }

            _savedOutput = new ImageFile(FilePath, fileToSave.ByteSize, fileToSave.Type);
            _wipInput = null;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"保存失败: {ex.Message}");
            return false;
        }
    }

    private bool _revertFileObjExists() => _revertFile != null;

    public bool CanRevert { get { lock (_lock) return CanRevertLocked(); } }

    /// <summary>回退到优化前的原始文件。</summary>
    public bool Revert()
    {
        lock (_lock)
        {
            if (!CanRevertLocked())
                return false;

            var revertFile = _revertFile!;
            try
            {
                File.Copy(revertFile.Path, FilePath, overwrite: true);
                if (_prefs.PreserveDates)
                {
                    File.SetCreationTime(FilePath, File.GetCreationTime(revertFile.Path));
                    File.SetLastWriteTime(FilePath, File.GetLastWriteTime(revertFile.Path));
                }

                _initialInput = new ImageFile(FilePath, revertFile.ByteSize, revertFile.Type);
                _unoptimizedInput = _initialInput;
                _wipInput = _initialInput;
                _savedOutput = null;
                _revertFile = null;
                _bestToolName = null;
                _bestTools.Clear();
                SetStatus("noopt", 6, "已还原到原始文件");
                Publish();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private bool IsStopping() { lock (_lock) return _stopping; }

    public bool Stop()
    {
        lock (_lock)
        {
            if (!_isDone && _workers.Count > 0)
            {
                _stopping = true;
                _workers.Clear();
                Publish();
                return true;
            }
        }
        return false;
    }

    public void Cleanup()
    {
        lock (_lock)
        {
            _workers.Clear();
            _wipInput = null;
        }
    }

    public void SetStatus(string image, int order, string text)
    {
        lock (_lock)
        {
            if (_isFailed && image != "ok" && image != "err")
                return;
            _statusOrder = order;
            _statusText = text;
            _statusImage = image;
        }
        Publish();
    }

    public void SetError(string text)
    {
        lock (_lock)
        {
            _isFailed = true;
            _statusOrder = 9;
            _statusText = text;
            _statusImage = "err";
        }
        Publish();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}