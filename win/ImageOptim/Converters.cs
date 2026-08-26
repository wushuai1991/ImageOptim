using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ImageOptim;

/// <summary>将状态图标名（wait/progress/ok/noopt/err）转换为显示符号。</summary>
public sealed class StatusToSymbolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (value as string) switch
        {
            "progress" => "●",
            "ok" => "✓",
            "noopt" => "—",
            "err" => "✕",
            _ => "○",
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>将状态图标名转换为对应颜色画刷。</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    private static readonly Brush WaitBrush = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
    private static readonly Brush ProgressBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xFF));
    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x9A, 0x4C));
    private static readonly Brush NooptBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    private static readonly Brush ErrBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0x33, 0x33));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (value as string) switch
        {
            "progress" => ProgressBrush,
            "ok" => OkBrush,
            "noopt" => NooptBrush,
            "err" => ErrBrush,
            _ => WaitBrush,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
