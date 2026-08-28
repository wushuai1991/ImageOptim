using System.Diagnostics;
using System.IO;
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

    // 输出目录缓存：不剔除原文件时，同一源目录下的文件输出到同一文件夹，避免每个文件各自新建
    private static readonly object _outputDirLock = new();
    private static readonly Dictionary<string, string> _outputDirCache = new(StringComparer.OrdinalIgnoreCase);

    private volatile JobSnapshot _snapshot = null!;
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
        if (optimizedFile == null || _unoptimizedInput == null || _unoptimizedInput == optimizedFile)
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

            // 依次运行各工具，每个产出结果立即比较采纳。
            // 注意：不能无脑删除 tempPath——如果这次产出被 SetFileOptimized 采纳，
            // _wipInput.Path 就指向 tempPath，删了会导致后续 SaveResultLocked 读不到文件。
            // 采纳链路里，前一个被采纳的临时文件会被 SetFileOptimized 自动清理；
            // 未被采纳的临时文件由此处 finally 兜底删除。
            foreach (var w in workers)
            {
                if (IsStopping()) break;

                var name = w.GetType().Name.Replace("Worker", "");
                SetStatus("progress", 4, $"开始 {name}");

                string tempPath = w.NewTempPath();
                try
                {
                    w.OptimizeFile(input, tempPath);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"{name} 失败: {ex.Message}");
                }
                finally
                {
                    // 仅当此次临时文件未被采纳（即不是当前最优结果）时才删除
                    bool adopted;
                    lock (_lock)
                    {
                        adopted = _wipInput != null &&
                                  string.Equals(_wipInput.Path, tempPath, StringComparison.OrdinalIgnoreCase);
                    }
                    if (!adopted)
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

        string? prevTempToDelete = null;
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

            // 若上一个 _wipInput 也是临时文件（非原始输入），需在采纳后删除，避免临时文件堆积
            if (_initialInput != null &&
                !string.Equals(oldFile.Path, _initialInput.Path, StringComparison.OrdinalIgnoreCase))
            {
                prevTempToDelete = oldFile.Path;
            }

            var newFile = new ImageFile(tempPath, newSize, oldFile.Type);
            _wipInput = newFile;

            _bestTools[toolName] = (newSize, (double)oldSize / newSize);
            UpdateBestToolName();
        }

        if (prevTempToDelete != null)
            TryDelete(prevTempToDelete);

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
        // 保存和缓存写入涉及磁盘 IO，不应持锁执行；分为「锁内判断+置位」与「锁外 IO」两阶段。
        bool needSave;
        string? tempToCleanup = null;
        lock (_lock)
        {
            needSave = IsOptimizedLocked();
            if (!needSave)
            {
                // 未产生更小结果：如果 _wipInput 是临时文件，需清理
                if (_wipInput != null && _initialInput != null &&
                    !string.Equals(_wipInput.Path, _initialInput.Path, StringComparison.OrdinalIgnoreCase))
                {
                    tempToCleanup = _wipInput.Path;
                }
                _wipInput = null;
                _isDone = true;
                _workers.Clear();
            }
        }

        if (needSave)
        {
            bool saved = SaveResultLocked();
            lock (_lock)
            {
                _isDone = true;
                _workers.Clear();
            }
            if (saved)
                SetStatus("ok", 7, $"已用 {_bestToolName} 成功优化");
            else
                SetError("优化后的文件无法保存");
        }
        else
        {
            if (tempToCleanup != null)
                TryDelete(tempToCleanup);

            SetStatus("noopt", 5, "文件无法进一步优化");

            bool shouldCache;
            lock (_lock) { shouldCache = !_stopping && !_isFailed; }
            if (shouldCache)
            {
                var fileHash = ResultsCache.ComputeHash(settingsHash, FilePath);
                _cache.MarkUnoptimizable(fileHash);
            }
        }
    }

    /// <summary>用临时优化结果替换原文件，并保留回退备份。</summary>
    /// <summary>
    /// 获取（或创建）源目录下的输出文件夹，用于「不剔除原文件」模式。
    /// 同一源目录共享同一个输出文件夹；若同名文件夹已存在则追加 _1、_2 … 数字后缀。
    /// </summary>
    private static string GetOutputDirectory(string sourceDir)
    {
        lock (_outputDirLock)
        {
            if (_outputDirCache.TryGetValue(sourceDir, out var cached))
                return cached;

            string candidate = System.IO.Path.Combine(sourceDir, "ImageOptim");
            if (!Directory.Exists(candidate))
            {
                Directory.CreateDirectory(candidate);
                _outputDirCache[sourceDir] = candidate;
                return candidate;
            }

            int i = 1;
            while (Directory.Exists(System.IO.Path.Combine(sourceDir, $"ImageOptim_{i}")))
                i++;
            candidate = System.IO.Path.Combine(sourceDir, $"ImageOptim_{i}");
            Directory.CreateDirectory(candidate);
            _outputDirCache[sourceDir] = candidate;
            return candidate;
        }
    }

    private bool SaveResultLocked()
    {
        ImageFile? fileToSave;
        ImageFile? original;
        bool removeOriginal;
        bool preserveDates;
        lock (_lock)
        {
            fileToSave = _wipInput;
            original = _unoptimizedInput;
        }
        removeOriginal = _prefs.RemoveOriginal;
        preserveDates = _prefs.PreserveDates;

        if (fileToSave == null || original == null)
            return false;

        string tempToCleanup = fileToSave.Path;
        try
        {
            var dir = System.IO.Path.GetDirectoryName(FilePath)!;

            if (!removeOriginal)
            {
                // 不剔除原文件：保存到新文件夹，文件名保持不变，原文件不动（无需备份/回退）
                var outputDir = GetOutputDirectory(dir);
                var outputPath = System.IO.Path.Combine(outputDir, System.IO.Path.GetFileName(FilePath));
                File.Copy(fileToSave.Path, outputPath, overwrite: true);

                lock (_lock)
                {
                    _savedOutput = new ImageFile(outputPath, fileToSave.ByteSize, fileToSave.Type);
                    _wipInput = null;
                }
                return true;
            }

            // 剔除原文件：覆盖原文件，保持名称不变（需备份以便回退）

            // 备份原文件到不与用户已有 .bak 冲突的路径
            string backupPath = ResolveUniqueBackupPath(dir, FilePath);
            File.Copy(FilePath, backupPath, overwrite: false);
            lock (_lock)
            {
                if (_revertFile == null)
                    _revertFile = new ImageFile(backupPath, original.ByteSize, original.Type);
            }

            // 保留原文件时间
            var creation = File.GetCreationTime(FilePath);
            var modified = File.GetLastWriteTime(FilePath);

            // 若目标是只读，先清除只读属性以允许覆盖
            try
            {
                var attrs = File.GetAttributes(FilePath);
                if ((attrs & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                    File.SetAttributes(FilePath, attrs & ~FileAttributes.ReadOnly);
            }
            catch { }

            // 用优化结果覆盖原文件
            File.Copy(fileToSave.Path, FilePath, overwrite: true);

            if (preserveDates)
            {
                try
                {
                    File.SetCreationTime(FilePath, creation);
                    File.SetLastWriteTime(FilePath, modified);
                }
                catch { /* 忽略时间戳设置失败 */ }
            }

            lock (_lock)
            {
                _savedOutput = new ImageFile(FilePath, fileToSave.ByteSize, fileToSave.Type);
                _wipInput = null;
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"保存失败: {ex.Message}");
            return false;
        }
        finally
        {
            // 保存链路结束后，若临时文件仍存在（说明它不是原始输入），清理掉
            if (original != null &&
                !string.Equals(tempToCleanup, original.Path, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(tempToCleanup, FilePath, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(tempToCleanup);
            }
        }
    }

    /// <summary>为备份文件寻找不冲突的路径，避免误删用户已有的同名 .bak。</summary>
    private static string ResolveUniqueBackupPath(string dir, string filePath)
    {
        string baseName = System.IO.Path.GetFileNameWithoutExtension(filePath);
        string ext = System.IO.Path.GetExtension(filePath);
        string candidate = System.IO.Path.Combine(dir, "~" + baseName + ".imageoptim.bak" + ext);
        int i = 1;
        while (File.Exists(candidate))
        {
            candidate = System.IO.Path.Combine(dir, "~" + baseName + ".imageoptim.bak." + i + ext);
            i++;
            if (i > 1000) break; // 安全保护
        }
        return candidate;
    }

    private bool _revertFileObjExists() => _revertFile != null;

    public bool CanRevert { get { lock (_lock) return CanRevertLocked(); } }

    /// <summary>回退到优化前的原始文件。</summary>
    public bool Revert()
    {
        ImageFile? revertFile;
        lock (_lock)
        {
            if (!CanRevertLocked())
                return false;
            revertFile = _revertFile;
        }
        if (revertFile == null)
            return false;

        try
        {
            // 若目标是只读，先清除只读属性
            try
            {
                var attrs = File.GetAttributes(FilePath);
                if ((attrs & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                    File.SetAttributes(FilePath, attrs & ~FileAttributes.ReadOnly);
            }
            catch { }

            File.Copy(revertFile.Path, FilePath, overwrite: true);
            if (_prefs.PreserveDates)
            {
                try
                {
                    File.SetCreationTime(FilePath, File.GetCreationTime(revertFile.Path));
                    File.SetLastWriteTime(FilePath, File.GetLastWriteTime(revertFile.Path));
                }
                catch { /* 忽略时间戳设置失败 */ }
            }

            lock (_lock)
            {
                _initialInput = new ImageFile(FilePath, revertFile.ByteSize, revertFile.Type);
                _unoptimizedInput = _initialInput;
                _wipInput = _initialInput;
                _savedOutput = null;
                _revertFile = null;
                _bestToolName = null;
                _bestTools.Clear();
                _isFailed = false; // 回退后清除失败状态，允许后续再次优化
            }
            SetStatus("noopt", 6, "已还原到原始文件");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"回退失败: {ex.Message}");
            return false;
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
        string? tempToCleanup = null;
        lock (_lock)
        {
            _workers.Clear();
            if (_wipInput != null && _initialInput != null &&
                !string.Equals(_wipInput.Path, _initialInput.Path, StringComparison.OrdinalIgnoreCase))
            {
                tempToCleanup = _wipInput.Path;
            }
            _wipInput = null;
        }
        if (tempToCleanup != null)
            TryDelete(tempToCleanup);
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