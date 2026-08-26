using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ImageOptim;

/// <summary>
/// Job 的可观察包装器，供 WPF 数据绑定使用。
/// 对应 macOS 原版的 <c>JobProxy</c>。
/// </summary>
public sealed class JobItem : INotifyPropertyChanged
{
    private readonly Job _job;
    private JobSnapshot _snapshot;

    public Job Job => _job;

    public JobItem(Job job)
    {
        _job = job;
        _snapshot = job.Snapshot;
        job.StateChanged += () =>
        {
            _snapshot = job.Snapshot;
            // 状态变化来自后台线程，需切回 UI 线程通知。
            // 合并为一次通知所有属性，避免逐项 OnPropertyChanged 引发 DataGrid 反复刷新。
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                OnPropertyChanged(string.Empty);
            });
        };
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
