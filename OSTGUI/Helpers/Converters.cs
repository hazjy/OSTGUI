using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace OSTGUI.Helpers;

/// <summary>
/// 主题相关颜色辅助（深色/浅色由窗口切换主题时同步）
/// </summary>
internal static class ThemeColorHelper
{
    public static bool IsDarkTheme { get; set; } = true;

    public static Microsoft.UI.Xaml.Media.SolidColorBrush DefaultTextBrush()
        => new(IsDarkTheme
            ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
            : Windows.UI.Color.FromArgb(255, 0x1B, 0x1B, 0x1B));
}

/// <summary>
/// Bool 取反转换器
/// </summary>
public class BoolNegateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b) return !b;
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b) return !b;
        return value;
    }
}

/// <summary>
/// Bool 到 Visibility 转换器
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b) return b ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is Visibility v) return v == Visibility.Visible;
        return false;
    }
}

/// <summary>
/// VersionMode 到颜色转换器
/// </summary>
public class VersionModeToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string mode)
        {
            return mode switch
            {
                "fixed" => new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 212)), // 蓝色
                _ => ThemeColorHelper.DefaultTextBrush()
            };
        }
        return ThemeColorHelper.DefaultTextBrush();
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 入库状态到颜色转换器（异常状态 → 红色，正常 → 默认文本色）
/// </summary>
public class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string status && status != "ok")
        {
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28));
        }

        return ThemeColorHelper.DefaultTextBrush();
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
