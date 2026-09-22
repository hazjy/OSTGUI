using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace OSTGUI.Helpers;

/// <summary>
/// 页面入场"上浮"动画：内容从下方浮起 + 淡入。
///
/// 为什么不用原生的 <c>EntranceThemeTransition</c>（2026-09-23 实测过）：那个主题过渡要等元素
/// **加载之后**才起动画，于是会先看到页面停在最终位置、再"跳回去往上浮"——用户看到的"抽一下"就是它。
///
/// 这里改成手写 Storyboard，关键在**初始状态写在 XAML 里**（`Opacity="0"` + `TranslateTransform.Y=40`）：
/// 第一帧就是不可见的，加载完再补间到 (1, 0)，中间没有"先到位再回退"那一帧。
/// 代价是初始值不能忘：页面 XAML 里必须保持那两处初始状态，否则动画会从可见处开始。
///
/// 一次性（每次进页面播一次）：页面是 `frame.Content = new XxxPage(...)` 新建的实例，
/// 所以每次切过去都会重播；将来若缓存页面实例，就得改这里。
/// </summary>
public static class PageEntrance
{
    /// <summary>起浮距离（逻辑像素）。想更含蓄调小、更明显调大</summary>
    public const double ShiftPx = 40;

    /// <summary>时长（毫秒）。比卡片悬浮的 220ms 略快一点，进页面不拖沓</summary>
    private const double DurationMs = 200;

    private static bool? _animationsEnabled;

    /// <summary>在页面的 `Loaded` 里调一次。元素上必须预先设好初始态：`Opacity=0` + 位移 <see cref="ShiftPx"/></summary>
    public static void Play(FrameworkElement root, TranslateTransform? shift)
    {
        try
        {
            if (shift is null || !AnimationsEnabled())
            {
                // 系统关掉动画 / XAML 里忘了写变换：直接落到终态，别把页面留在不可见处
                root.Opacity = 1;
                if (shift is not null) shift.Y = 0;
                return;
            }

            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var storyboard = new Storyboard();

            var slide = new DoubleAnimation
            {
                From = shift.Y,          // 以 XAML 里设的初始位移为准
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(DurationMs)),
                EasingFunction = easing
            };
            Storyboard.SetTarget(slide, shift);
            Storyboard.SetTargetProperty(slide, "Y");
            storyboard.Children.Add(slide);

            var fade = new DoubleAnimation
            {
                From = root.Opacity,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(DurationMs)),
                EasingFunction = easing
            };
            Storyboard.SetTarget(fade, root);
            Storyboard.SetTargetProperty(fade, "Opacity");
            storyboard.Children.Add(fade);

            storyboard.Begin();
        }
        catch
        {
            root.Opacity = 1;           // 动画失败不能让页面留在看不见的状态
            if (shift is not null) shift.Y = 0;
        }
    }

    private static bool AnimationsEnabled()
    {
        if (_animationsEnabled is null)
        {
            try { _animationsEnabled = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled; }
            catch { _animationsEnabled = true; }
        }
        return _animationsEnabled.Value;
    }
}
