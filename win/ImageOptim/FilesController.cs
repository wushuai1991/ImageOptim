using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace ImageOptim;

/// <summary>
/// 文件列表 ViewModel。对应 macOS 原版的 <c>FilesController</c>。
/// 管理待优化文件列表，处理添加、去重、停止、重试、清理、回退等操作。
/// </summary>
public sealed class FilesController
{
    private readonly JobQueue _queue;
    private readonly Preferences _prefs;
    private readonly HashSet<string> _seenPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, JobItem> _jobByPath = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<JobItem> Items { get; } = new();

    private bool _isStoppable;
    public bool IsStoppable
    {
        get => _isStoppable;
        set => _isStoppable = value;
    }

    public FilesController(JobQueue queue, Preferences prefs)
    {
        _queue = queue;
        _prefs = prefs;
        _queue.BusyStateChanged += () => Application.Current.Dispatcher.Invoke(UpdateStoppableState);
        _queue.QueueFinished += () => Application.Current.Dispatcher.Invoke(UpdateStoppableState);
    }

    /// <summary>添加一批路径（文件或目录）。</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        var filePaths = new List<string>();
        var dirPaths = new List<string>();

        foreach (var p in paths)
        {
            if (Directory.Exists(p))
                dirPaths.Add(p);
            else if (File.Exists(p))
                filePaths.Add(p);
        }

        // 先处理文件
        foreach (var path in filePaths)
            AddSingleFile(path);

        // 再处理目录（异步扫描）
        foreach (var dir in dirPaths)
        {
            _queue.AddDirectory(dir, foundFiles =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    foreach (var f in foundFiles)
                        AddSingleFile(f);
                });
            });
        }
    }

    private void AddSingleFile(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (!_seenPaths.Add(full))
        {
            // 已存在且不忙，重新入队
            if (_jobByPath.TryGetValue(full, out var existing) && !existing.IsBusy)
                _queue.Requeue(existing.Job);
            return;
        }

        var job = _queue.AddFile(full);
        var item = new JobItem(job);
        _jobByPath[full] = item;
        Items.Add(item);
        UpdateStoppableState();
    }

    public void StopSelected(IEnumerable<JobItem> selected)
    {
        foreach (var item in selected)
            item.Job.Stop();
        UpdateStoppableState();
    }

    public void RevertSelected(IEnumerable<JobItem> selected)
    {
        foreach (var item in selected)
            item.Job.Revert();
    }

    public void StartAgain(IEnumerable<JobItem> selected, bool optimizedOnly)
    {
        bool any = false;
        foreach (var item in selected)
        {
            if (!item.IsBusy && (!optimizedOnly || item.IsOptimized))
            {
                _queue.Requeue(item.Job);
                any = true;
            }
        }
        if (!any)
            Console.Beep();
    }

    public void ClearComplete()
    {
        var toRemove = Items.Where(i => i.IsDone).ToList();
        foreach (var item in toRemove)
        {
            Items.Remove(item);
            _seenPaths.Remove(item.FilePath);
            _jobByPath.Remove(item.FilePath);
        }
    }

    public bool CanClearComplete => Items.Any(i => i.IsDone);

    public bool CanRevert(IEnumerable<JobItem> selected) => selected.Any(i => i.CanRevert);

    private void UpdateStoppableState()
    {
        IsStoppable = _queue.IsBusy;
    }

    public void Cleanup()
    {
        foreach (var item in Items)
            item.Job.Cleanup();
    }
}