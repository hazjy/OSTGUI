using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace OSTGUI.Helpers;

/// <summary>
/// 卡片悬浮微交互：高亮 + 轻微放大 + 投影。
///
/// 用 WinUI 原生件做：`ElementCompositionPreview` 的隐式动画（改属性即自动补间，进出都顺）
/// + `ThemeShadow`（跟着 Z 深度走的系统投影，自动适配深浅主题），不引第三方库、不重写
/// `ListViewItem`/`GridViewItem` 的 ControlTemplate（那要自己接管焦点/拖拽/选择等内置行为，回归面太大）。
///
/// 作用对象分两处，别搞混：
///   - **缩放 / 位移 / 阴影** 打在**容器**（`GridViewItem` / `ListViewItem`）上 —— 卡片的 DataTemplate
///     会被回收复用，而容器是稳定的，回收时才有东西可复位；
///   - **背景高亮** 打在**卡片本身**上（容器在卡片底下，被不透明的卡片盖住，改它看不见）。
///
/// 数值与画刷同步记在 `doc/细节与偏好.md`
/// </summary>
public static class CardHover
{
    // 网格卡片是窄卡，放大明显一点；列表卡片横跨整行，放大一点点就够
    public const float GridScale = 1.03f;
    public const float ListScale = 1.01f;
    public const float GridShadowZ = 24f;
    public const float ListShadowZ = 16f;

    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(140);

    private sealed class State
    {
        public Brush? NormalBackground;   // 首次悬浮时读一次，移开还原
        public ThemeShadow? Shadow;       // 只在悬浮期间挂上，不常驻
    }

    // 以**容器**为键：卡片元素会被回收重建，容器才是稳定的
    private static readonly ConditionalWeakTable<FrameworkElement, State> States = new();

    private static bool? _animationsEnabled;

    /// <summary>系统"关闭动画效果"时不做补间（属性照改，只是跳变）</summary>
    private static bool AnimationsEnabled
    {
        get
        {
            if (_animationsEnabled is null)
            {
                try { _animationsEnabled = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled; }
                catch { _animationsEnabled = true; }
            }
            return _animationsEnabled.Value;
        }
    }

    /// <summary>指针进入卡片</summary>
    public static void Enter(FrameworkElement container, Border card, Brush? hoverBackground, float scale, float shadowZ)
    {
        var state = States.GetOrCreateValue(container);
        state.NormalBackground ??= card.Background;

        // ⚠️ 顺序要紧：**先设属性、再装动画**。动画只是"补间"，是锦上添花；
        // 之前反过来写，装动画那句一旦抛异常就被 catch 吞掉，属性根本没设上 → 整个交互静默失效
        try
        {
            // Translation 要先在这个元素上启用（否则设了不生效，阴影也出不来）
            ElementCompositionPreview.SetIsTranslationEnabled(container, true);
            container.Scale = new Vector3(scale, scale, 1f);
            container.Translation = new Vector3(0f, 0f, shadowZ);
            if (hoverBackground is not null) card.Background = hoverBackground;

            state.Shadow ??= new ThemeShadow();
            container.Shadow = state.Shadow;
        }
        catch (Exception ex) { Log($"【诊断】设属性失败: {ex.GetType().Name} {ex.Message}"); }

        try
        {
            EnsureImplicitAnimations(container);
        }
        catch (Exception ex) { Log($"【诊断】装隐式动画失败: {ex.GetType().Name} {ex.Message}"); }

        Log("【诊断】enter");   // 诊断期临时日志：确认事件是否触发
    }

    /// <summary>指针离开卡片</summary>
    public static void Exit(FrameworkElement container, Border? card)
    {
        try
        {
            if (States.TryGetValue(container, out var state))
            {
                if (card is not null && state.NormalBackground is not null)
                    card.Background = state.NormalBackground;
                container.Shadow = null;   // 阴影只在悬浮时挂着
            }

            container.Scale = Vector3.One;
            container.Translation = Vector3.Zero;
        }
        catch (Exception ex) { Log($"【诊断】复位失败: {ex.GetType().Name} {ex.Message}"); }
    }

    /// <summary>
    /// 容器被回收时复位（卡片滚出视口时指针可能还压着它——不复位的话，回收后的容器
    /// 会带着高亮与缩放跳到别的位置去）。卡片自身的背景不用管：容器换条目时
    /// ContentPresenter 会按模板重建一份新的卡片元素
    /// </summary>
    public static void Reset(FrameworkElement container) => Exit(container, null);

    /// <summary>给元素视觉装一次隐式动画：之后改 Scale / Translation 就自动补间</summary>
    private static void EnsureImplicitAnimations(FrameworkElement element)
    {
        if (!AnimationsEnabled) return;

        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (visual.ImplicitAnimations is not null) return;   // 装过就不再装（元素自己就是判据，不用额外记账）

        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f));

        var animations = compositor.CreateImplicitAnimationCollection();

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertExpressionKeyFrame(1f, "this.FinalValue", easing);
        scale.Duration = Duration;
        animations["Scale"] = scale;

        var translation = compositor.CreateVector3KeyFrameAnimation();
        translation.InsertExpressionKeyFrame(1f, "this.FinalValue", easing);
        translation.Duration = Duration;
        animations["Translation"] = translation;

        visual.ImplicitAnimations = animations;
    }

    /// <summary>诊断期临时日志：写进应用日志文件（设置页也能看），定位完就撤</summary>
    private static void Log(string message) => OSTGUI.Services.LogService.AddAppLog($"[Hover] {message}");
}
