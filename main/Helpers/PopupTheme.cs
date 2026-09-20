using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OSTGUI.Helpers;

/// <summary>
/// 代码创建的 ContentDialog 需要显式告诉它用哪个主题。
///
/// 原因（2026-09-20 实测，截图核对）：**XAML 里声明**的弹窗挂在页面树里，会从页面继承
/// `RootGrid.RequestedTheme`；而 **代码 `new` 出来**的弹窗不在 XAML 树里，主题落到**系统主题**——
/// 浅色应用 + 深色系统时整片发黑（账号弹窗就是这个症状，用户实测截图确认）。
///
/// 只设主题、不碰背景与画刷：背景交回框架默认，弹窗内容的画刷走各自 XAML 里的 {ThemeResource} 取样点。
/// XAML 里声明的弹窗不用调这个（继承即可）。
/// </summary>
internal static class PopupTheme
{
    public static void Apply(ContentDialog dialog)
        => dialog.RequestedTheme = ThemeColorHelper.IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light;
}
