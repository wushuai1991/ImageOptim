using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;

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
        _queue.BusyStateChanged += () => Application.Current.Dispatcher.BeginInvoke(UpdateStoppableState);
        _queue.QueueFinished += () => Application.Current.Dispatcher.BeginInvoke(UpdateStoppableState);
    }

    /// <summary>添加一批路径（文件或目录）。分类在后台线程完成，避免 UI 线程做文件系统 IO。</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        var pathList = paths.ToList();
        Task.Run(() =>
        {
            var filePaths = new List<string>();
            var dirPaths = new List<string>();

            foreach (var p in pathList)
            {
                if (Directory.Exists(p))
                    dirPaths.Add(p);
                else if (File.Exists(p))
                    filePaths.Add(p);
            }

            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                AddFilePaths(filePaths);
                foreach (var dir in dirPaths)
                    AddDirectoryPath(dir);
            });
        });
    }

    /// <summary>批量创建并添加文件条目（UI 线程调用）。</summary>
    private void AddFilePaths(IReadOnlyList<string> filePaths)
    {
        // 批量构建后一次性加入列表，避免逐个 Items.Add 反复刷新 DataGrid 导致 UI 卡死
        var newItems = new List<JobItem>();
        foreach (var path in filePaths)
        {
            var item = TryCreateItem(path);
            if (item != null)
                newItems.Add(item);
        }
        if (newItems.Count > 0)
        {
            foreach (var item in newItems)
                Items.Add(item);
            UpdateStoppableState();
        }
    }

    /// <summary>添加一个目录（异步扫描，分批回调）。</summary>
    private void AddDirectoryPath(string dir)
    {
        _queue.AddDirectory(dir, foundFiles =>
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                var batch = new List<JobItem>();
                foreach (var f in foundFiles)
                {
                    var item = TryCreateItem(f);
                    if (item != null)
                        batch.Add(item);
                }
                foreach (var item in batch)
                    Items.Add(item);
                if (batch.Count > 0)
                    UpdateStoppableState();
            });
        });
    }

    /// <summary>创建（或复用）单个文件的 JobItem；去重时返回 null。</summary>
    private JobItem? TryCreateItem(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (!_seenPaths.Add(full))
        {
            // 已存在且不忙，重新入队
            if (_jobByPath.TryGetValue(full, out var existing) && !existing.IsBusy)
                _queue.Requeue(existing.Job);
            return null;
        }

        var job = _queue.AddFile(full);
        var item = new JobItem(job);
        _jobByPath[full] = item;
        return item;
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
            item.Detach();
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
        {
            item.Detach();
            item.Job.Cleanup();
        }
    }
}