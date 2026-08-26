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

        // 状态栏定时刷新
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _statusTimer.Tick += (_, _) => UpdateStatusBar();
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

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (ConfirmAddPaths(paths))
                _files.AddPaths(paths);
        }
    }

    private bool ConfirmAddPaths(string[] paths)
    {
        int threshold = _prefs.AddFilesThreshold;
        int count = _queue.CountFiles(paths, threshold);
        if (count > threshold)
        {
            var result = MessageBox.Show(
                this,
                $"本次将添加 {count} 个文件，已超过提醒阈值（{threshold}）。是否继续？",
                "确认添加文件",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            return result == MessageBoxResult.Yes;
        }
        return true;
    }

    private void BrowseFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Title = "选择要优化的图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.gif;*.svg|所有文件|*.*",
        };
        if (dialog.ShowDialog() == true)
        {
            if (ConfirmAddPaths(dialog.FileNames))
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
        base.OnClosed(e);
    }
}
