using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace ImageOptim;

/// <summary>
/// Job 的可观察包装器，供 WPF 数据绑定使用。
/// 对应 macOS 原版的 <c>JobProxy</c>。
/// </summary>
public sealed class JobItem : INotifyPropertyChanged
{
    private readonly Job _job;
    private volatile JobSnapshot _snapshot;
    private readonly Action _stateChangedHandler;

    // 0 = 没有排队的 UI 通知，1 = 已排一条。用来把同一文件的一串状态变化合并成一次重绑。
    private int _notifyPending;

    public Job Job => _job;

    public JobItem(Job job)
    {
        _job = job;
        _snapshot = job.Snapshot;
        // 记录处理器引用，便于清理时解绑，避免长期驻留导致内存泄漏与无谓 UI 通知
        _stateChangedHandler = () =>
        {
            _snapshot = job.Snapshot;

            // 合并：同一文件在 UI 线程处理之前又变了多次，也只需要一次重绑。
            // 1 万个文件 × 每文件 4~8 次状态变化，不合并会产生数万条 Dispatcher 队列项。
            if (Interlocked.Exchange(ref _notifyPending, 1) == 1)
                return;

            // 状态变化来自后台线程，需切回 UI 线程通知。
            // 使用异步 BeginInvoke（而非同步 Invoke），避免后台线程等待 UI 线程，
            // 与 UI 线程访问 Job 锁形成死锁。
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                Interlocked.Exchange(ref _notifyPending, 0);
                return;
            }

            dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                Interlocked.Exchange(ref _notifyPending, 0);
                OnPropertyChanged(string.Empty);
            }));
        };
        job.StateChanged += _stateChangedHandler;
    }

    /// <summary>解绑事件订阅（在 JobItem 从列表移除时调用）。</summary>
    public void Detach()
    {
        _job.StateChanged -= _stateChangedHandler;
    }

    public string FilePath => _snapshot.FilePath;
    public string DisplayName => _snapshot.DisplayName;
    public string FileName => System.IO.Path.GetFileName(_snapshot.FilePath);
    public string StatusText => _snapshot.StatusText;
    public string? BestToolName => _snapshot.BestToolName;
    public string StatusImage => _snapshot.StatusImage;
    public bool IsDone => _snapshot.IsDone;
    public bool IsBusy => _snapshot.IsBusy;
    public bool IsFailed => _snapshot.IsFailed;
    public bool CanRevert => _snapshot.CanRevert;
    public bool IsOptimized => _snapshot.IsOptimized;

    public double? PercentOptimized => _snapshot.PercentOptimized;
    public string PercentOptimizedText => FormatPercent(_snapshot.PercentOptimized);

    public string ByteSizeOriginalText => FormatBytes(_snapshot.ByteSizeOriginal);
    public string ByteSizeOptimizedText => FormatBytes(_snapshot.ByteSizeOptimized);

    public static string FormatBytes(long? bytes)
    {
        if (bytes == null) return "";
        return BytesToString(bytes.Value);
    }

    public static string BytesToString(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        if (unit == 0) return $"{bytes} {units[unit]}";
        return $"{value:0.0} {units[unit]}";
    }

    public static string FormatPercent(double? p)
    {
        if (p == null) return "";
        return $"{p.Value:0.0}%";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
