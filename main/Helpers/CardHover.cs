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
    private static TimeSpan Duration
    {
        get
        {
            if (_animationsEnabled is null)
            {
                _animationsEnabled = ReadAnimationsEnabled();
                // 只记一次：系统动画关掉时 Duration=0，没有补间，缩小"只加了日志还是查不出"的余地
                if (!_animationsEnabled.Value) Log("系统动画已关（Duration=0），放大不补间");
            }
            return _animationsEnabled.Value ? TimeSpan.FromMilliseconds(DurationMs) : TimeSpan.Zero;
        }
    }

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
        // ⚠️ 这里**不能**用 `is` / `as` / 硬转来判类型：模板里那个 ScaleTransform 是 XAML 侧建的，
        // AOT 下 `card.RenderTransform is ScaleTransform` 会**静默判负**（CsWinRT #2516 / #2475 / #2536
        // 同一类：vtable 查表不匹配、不抛异常）—— 原来那句判定 + return 就是"高亮阴影都正常、
        // 只有放大不生效"的全部原因（2026-09-27）。硬转同样不行：本 App 的 AOT 构建里
        // `(MenuFlyout)Resources["MoreMenu"]` 就在抛 InvalidCastException。
        // 所以只判 null：模板里挂了就直接拿来用，没有就补一个（判定结果由下面那条日志暴露）。
        // 目标对象 + 属性路径由 XAML 运行时自己解析（不走 CLR 类型），照样匹配得到 ScaleX / ScaleY。
        var transform = card.RenderTransform ??= new ScaleTransform();

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

        // 动画跑完才说明"目标匹配成功"；只打了 Begin 那条却没等到 Completed，
        // 就能区分出"匹配失败"与"动画跑了但没效果"。
        // 不读 transform.ScaleX —— 那是 CLR 强类型属性，跑一次就要一次类型判定（见上面的 AOT 坑），
        // 卡片的缩放肉眼即可验证，这里只记类型。
        storyboard.Completed += (_, _) => Log($"放大完成：{to:F3} 变换 {transform.GetType().FullName}");

        // 先记一条（Begin 之前）：变换类型只要不是 ScaleTransform，这里直接暴露
        Log($"放大：{to:F3} 变换类型 {transform.GetType().FullName} 时长 {Duration.TotalMilliseconds:F0}ms");

        storyboard.Begin();

        // 系统关掉动画时 Duration 是 0 —— 补间没了，只会立刻到位
        if (Duration == TimeSpan.Zero) Log($"放大即时生效（系统动画已关）：{to:F3}");
    }

    private static bool ReadAnimationsEnabled()
    {
        try { return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled; }
        catch { return true; }
    }

    private static void Log(string message) => OSTGUI.Services.LogService.Diag($"[Hover] {message}");
}
