using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ImageOptim;

public partial class MainWindow : Window
{
    private readonly Preferences _prefs;
    private readonly ResultsCache _cache;
    private readonly JobQueue _queue;
    private readonly FilesController _files;
    private readonly StatusBarCalculator _statusCalc = new();
    private readonly DispatcherTimer _statusTimer;

    // 状态栏只在"确实有变化"时重算，空闲时定时器不做任何遍历
    private bool _statusDirty = true;

    public ObservableCollection<JobItem> Items => _files.Items;

    public MainWindow()
    {
        InitializeComponent();

        _prefs = Preferences.Load();
        _cache = new ResultsCache();
        _queue = new JobQueue(_prefs, _cache);
        _files = new FilesController(_queue, _prefs);

        DataContext = this;
        Topmost = _prefs.WindowTopmost;

        _queue.BusyStateChanged += () => Dispatcher.BeginInvoke(UpdateStatusBar);
        _queue.QueueFinished += () => Dispatcher.BeginInvoke(OnQueueFinished);

        // 列表成员或任一条目状态变化时打脏标记（事件本身已在 UI 线程聚合）
        _files.ItemsChanged += () => _statusDirty = true;

        // 状态栏定时刷新（500ms 足够平滑）。空闲时 _statusDirty 为 false，直接跳过遍历。
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += (_, _) =>
        {
            if (!_statusDirty)
                return;
            UpdateStatusBar();
        };
        _statusTimer.Start();

        UpdateStatusBar();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            var (proceed, preScanned) = await ConfirmAddPathsAsync(paths);
            if (!proceed)
                return;

            if (preScanned != null)
                _files.AddScannedFiles(preScanned.Files);
            else
                _files.AddPaths(paths);
        }
    }

    /// <summary>
    /// 添加前的扫描与确认。
    /// 返回的 PreScanned 在"未超阈值"时非空，可直接复用来添加，
    /// 省掉正式添加时的第二遍目录枚举（旧实现是统计一遍 + 扫描一遍）。
    /// </summary>
    private async Task<(bool Proceed, JobQueue.ScanResult? PreScanned)> ConfirmAddPathsAsync(string[] paths)
    {
        int threshold = _prefs.AddFilesThreshold;

        // 扫描在后台线程执行，避免拖入大目录时在 UI 线程递归遍历导致卡顿
        JobQueue.ScanResult scan;
        try
        {
            scan = await Task.Run(() => _queue.Scan(paths, threshold));
        }
        catch
        {
            return (true, null); // 扫描失败不拦截，退回原有的异步扫描路径
        }

        if (scan.Files.Count > threshold)
        {
            var result = MessageBox.Show(
                this,
                $"本次将添加超过 {threshold} 个文件（已扫描到 {scan.Files.Count} 个）。是否继续？",
                "确认添加文件",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            return (result == MessageBoxResult.Yes, null);
        }

        // Truncated（超上限或目录异常）时结果不完整，交给正式扫描重来
        return (true, scan.Truncated ? null : scan);
    }

    private async void BrowseFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Title = "选择要优化的图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.gif;*.svg|所有文件|*.*",
        };
        if (dialog.ShowDialog() == true)
        {
            var (proceed, preScanned) = await ConfirmAddPathsAsync(dialog.FileNames);
            if (!proceed)
                return;

            if (preScanned != null)
                _files.AddScannedFiles(preScanned.Files);
            else
                _files.AddPaths(dialog.FileNames);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _files.StopSelected(FileGrid.SelectedItems.Cast<JobItem>());
    }

    private void StartAgain_Click(object sender, RoutedEventArgs e)
    {
        var selected = FileGrid.SelectedItems.Cast<JobItem>().ToList();
        if (selected.Count == 0)
            selected = _files.Items.ToList();
        bool optimizedOnly = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        _files.StartAgain(selected, optimizedOnly);
    }

    private void ClearComplete_Click(object sender, RoutedEventArgs e)
    {
        _files.ClearComplete();
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        _files.RevertSelected(FileGrid.SelectedItems.Cast<JobItem>());
    }

    private void ShowPrefs_Click(object sender, RoutedEventArgs e)
    {
        var prefsWindow = new PrefsWindow(_prefs);
        prefsWindow.Owner = this;
        prefsWindow.ShowDialog();
        _prefs.Save();
        Topmost = _prefs.WindowTopmost;
        UpdateStatusBar();
    }

    private void UpdateStatusBar()
    {
        _statusDirty = false;
        if (StatusText != null)
            StatusText.Text = _statusCalc.Calculate(_files.Items, _prefs, quitWhenDone: false);
    }

    private void OnQueueFinished()
    {
        if (_prefs.BounceDock)
        {
            // 在 Windows 下通过任务栏闪烁提示
            Activate();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _statusTimer.Stop();
        _files.Cleanup();
        _prefs.Save();
        _cache.Flush();
        base.OnClosed(e);
    }
}
