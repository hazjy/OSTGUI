using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace OSTGUI.Helpers;

/// <summary>
/// 卡片悬浮微交互：高亮 + 轻微放大 + 投影。
///
/// **纯 XAML 那套经典实现**，刻意不碰 Composition 的 visual 所有权：
///   - 放大：卡片模板里挂一个 <see cref="ScaleTransform"/>（`RenderTransformOrigin="0.5,0.5"`），
///     这里用代码建 <see cref="Storyboard"/> 动画（140ms 缓出，进出都补间）
///   - 投影：`UIElement.Shadow` + `Translation.Z`（XAML 属性，系统投影，自动适配深浅主题）
///   - 高亮：换卡片背景刷（`CardBackgroundFillColorSecondaryBrush`）；移开时用 `ClearValue` **退回 Style 里的
///     主题引用**（卡片模板的 `Background` 必须写在 Style 上，原因见 <see cref="Exit"/>）
///
/// ⚠️ **千万不要**在同一个元素上再调 `ElementCompositionPreview.GetElementVisual`（例如为了装
/// 隐式动画）：一旦调过，XAML 的 `Scale` / `Translation` / `Shadow` 会全部抛
/// `UnauthorizedAccessException: ... the ElementCompositionPreview.GetElementVisual property in use`
/// ——2026-09-23 就是这么踩的：事件照常触发、每一步都异常、全被 catch 吞掉，表现成"悬浮什么反应都没有"。
/// 要用 Composition 做动画就整套都用 Composition（阴影也得换成 composition DropShadow），两边不能混。
///
/// 数值与画刷同步记在 `doc/细节与偏好.md`
/// </summary>
public static class CardHover
{
    // 网格卡片是窄卡，放大明显一点；列表卡片横跨整行，放大一点点就够
    // ⚠️ 网格 1.06 已接近上限：卡片四边只有 6 逻辑像素余量（容器边距），再大就要被视口切边
    public const float GridScale = 1.06f;
    public const float ListScale = 1.01f;
    public const float GridShadowZ = 24f;
    public const float ListShadowZ = 16f;

    /// <summary>放大/回落的时长（毫秒）。调大 = 更慢更飘逸</summary>
    private const double DurationMs = 220;

    private sealed class State
    {
        public Storyboard? Running;       // 留住引用：交给 GC 有可能半路停掉
        public ThemeShadow? Shadow;
    }

    private static readonly ConditionalWeakTable<Border, State> States = new();

    private static bool? _animationsEnabled;
    private static TimeSpan Duration =>
        (_animationsEnabled ??= ReadAnimationsEnabled()) ? TimeSpan.FromMilliseconds(DurationMs) : TimeSpan.Zero;

    /// <summary>指针进入卡片</summary>
    public static void Enter(Border card, Brush? hoverBackground, float scale, float shadowZ)
    {
        var state = States.GetOrCreateValue(card);

        Animate(card, state, scale);

        // 高亮、阴影各自独立兜底：任何一步失败都不影响其它步骤（别再一次 try 全包住）
        if (hoverBackground is not null)
        {
            try { card.Background = hoverBackground; }
            catch (Exception ex) { Log($"换高亮背景失败: {ex.GetType().Name} {ex.Message}"); }
        }

        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(card, true);   // 启用 Translation（不启用了设了也没用）
            state.Shadow ??= new ThemeShadow();
            card.Shadow = state.Shadow;
            card.Translation = new Vector3(0f, 0f, shadowZ);
        }
        catch (Exception ex) { Log($"阴影失败: {ex.GetType().Name} {ex.Message}"); }
    }

    /// <summary>指针离开卡片</summary>
    public static void Exit(Border card)
    {
        if (!States.TryGetValue(card, out var state)) return;

        Animate(card, state, 1f);

        try
        {
            // ⚠️ 用 ClearValue 收回本地值，退回到 Style 里那条「{ThemeResource}」引用 —— 卡片模板的
            // Background 必须写在 Style（而不是 Border 属性）上，就是为了这一步能退得回去。
            // **千万别**改成"悬浮前把 card.Background 存下来、移开写回去"：那存的是一个已经解析好的
            // 裸画刷实例，写回就把主题引用顶掉了，之后切主题这张卡片再也不跟 —— 2026-09-23 用户报的
            // "深色主题下卡片还是浅色底、字却变白了"就是这么来的（被悬浮过的卡片全中，没碰过的正常）。
            card.ClearValue(Border.BackgroundProperty);
            card.Shadow = null;          // 阴影只在悬浮时挂着
            card.Translation = Vector3.Zero;
        }
        catch (Exception ex) { Log($"复原失败: {ex.GetType().Name} {ex.Message}"); }
    }

    /// <summary>把卡片缩放到 <paramref name="to"/>（进出都走这里，只有目标值不同）</summary>
    private static void Animate(Border card, State state, float to)
    {
        if (card.RenderTransform is not ScaleTransform transform) return;

        var storyboard = new Storyboard();
        foreach (var property in new[] { "ScaleX", "ScaleY" })
        {
            var animation = new DoubleAnimation
            {
                To = to,
                Duration = Duration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, transform);
            Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
        }

        state.Running?.Stop();
        state.Running = storyboard;
        storyboard.Begin();
    }

    private static bool ReadAnimationsEnabled()
    {
        try { return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled; }
        catch { return true; }
    }

    private static void Log(string message) => OSTGUI.Services.LogService.AddAppLog($"[Hover] {message}");
}
