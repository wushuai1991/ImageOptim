using System.Windows;

namespace ImageOptim;

public partial class PrefsWindow : Window
{
    private readonly Preferences _prefs;

    public PrefsWindow(Preferences prefs)
    {
        InitializeComponent();
        _prefs = prefs;
        DataContext = prefs;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        // 数值文本框校验
        if (int.TryParse(TxtConcurrent.Text, out int concurrent))
            _prefs.RunConcurrentFiles = Math.Max(1, concurrent);

        // Guetzli 联动：启用时提高 JPEG 质量下限并强制剥离元数据
        if (_prefs.GuetzliEnabled)
        {
            if (_prefs.JpegOptimMaxQuality < 85)
                _prefs.JpegOptimMaxQuality = 85;
            _prefs.JpegTranStripAll = true;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
