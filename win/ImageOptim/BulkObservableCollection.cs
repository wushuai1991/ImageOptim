using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ImageOptim;

/// <summary>
/// 支持批量增删的 <see cref="ObservableCollection{T}"/>。
/// WPF 的 ObservableCollection 没有 AddRange，逐条 Add 会让 DataGrid 每条都重排一次，
/// 添加 1 万个文件时基本卡死。这里先改底层列表，最后只发一次集合变更通知。
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotification;

    /// <summary>批量添加：只发一次 Add 通知（保留选区，不像 Reset 那样整表重建）。</summary>
    public void AddRange(IEnumerable<T> items)
    {
        var added = items as IList<T> ?? items.ToList();
        if (added.Count == 0)
            return;

        int startIndex = Count;

        _suppressNotification = true;
        try
        {
            foreach (var item in added)
                Items.Add(item);
        }
        finally
        {
            _suppressNotification = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add, added.ToList(), startIndex));
    }

    /// <summary>批量删除：被删项通常不连续，统一发一次 Reset。</summary>
    public void RemoveRange(IEnumerable<T> items)
    {
        var removed = items as IList<T> ?? items.ToList();
        if (removed.Count == 0)
            return;

        _suppressNotification = true;
        try
        {
            foreach (var item in removed)
                Items.Remove(item);
        }
        finally
        {
            _suppressNotification = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotification)
            base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_suppressNotification)
            base.OnPropertyChanged(e);
    }
}
