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

        // ── 复用（2026-09-27）：进出两条 storyboard 一次建好、反复 Begin，热路径上零分配 ──
        // Bound = 建它们时绑定的那个 Transform；**只用引用相等判断**，不做任何类型判定
        // （理由见 Animate() 里那段：AOT 下类型判定会静默判负）
        public Transform? Bound;
        public float InTo;                // In 那条 storyboard 当前的目标缩放（判断要不要重建 ✓）
        public Storyboard? In;
        public Storyboard? Out;
        public DoubleAnimation? InX;
        public DoubleAnimation? InY;
        public DoubleAnimation? OutX;
        public DoubleAnimation? OutY;
    }

    /// <summary>
    /// 详细日志开关（默认关 ✗）。悬浮是**热路径**，每次进出都落盘会疯狂写文件（用户实测
    /// 触发几次后卡顿 + 内存飙升，2026-09-27）；只有排查时才置 true，会输出
    /// "放大：…" 与 "放大完成：…" 两条（后者只在创建 storyboard 时订阅一次，不会累积 ✓）。
    /// </summary>
    internal static bool Trace { get; set; }

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

        // ── 复用：两条 storyboard 一次建好（进/出各一条），之后直接 Begin ✓ 热路径零分配 ──
        // 只在"目标变换换了"或"进的目标值变了"时重建；判定只用**引用相等 / 值比较**，
        // 不涉及 CLR 类型判定 ✓（transform 是本类拿到的 WinUI 对象）
        // ⚠️ 刻意**不在每次 Begin 前改 animation.To**：那是 WinRT 属性，写一次要包一次
        //    `IReference<double>` ✗（等于每个方向两次小分配）。改成"进的目标值写进缓存的
        //    storyboard"，稳态下连属性写都没有 ✓
        var isExit = MathF.Abs(to - 1f) < 0.0001f;
        if (state.In is null
            || !ReferenceEquals(state.Bound, transform)
            || (!isExit && MathF.Abs(state.InTo - to) > 0.0001f))
        {
            state.Bound = transform;
            state.InTo = isExit ? 1f : to;
            (state.In, state.InX, state.InY) = CreateScaleStoryboard(transform, state.InTo);
            (state.Out, state.OutX, state.OutY) = CreateScaleStoryboard(transform, 1f);
        }

        var storyboard = (isExit ? state.Out : state.In)!;

        state.Running?.Stop();
        state.Running = storyboard;

        if (Trace)
            Log($"放大：{to:F3} 变换类型 {transform.GetType().FullName} 时长 {Duration.TotalMilliseconds:F0}ms");

        storyboard.Begin();

        // 系统关掉动画时 Duration 是 0 —— 补间没了，只会立刻到位
        if (Trace && Duration == TimeSpan.Zero) Log($"放大即时生效（系统动画已关）：{to:F3}");
    }

    /// <summary>
    /// 建"缩放到 to（ScaleX + ScaleY）"的一条 storyboard。只在**首次**或目标变换换了时调用 ✓
    /// 不在热路径上分配 ✓；<c>To</c> 之后每次 Begin 前按需改写 ✓。
    /// Trace 打开时在这里订一次 <c>Completed</c> —— **绝不**在 Animate 里每次 `+=`（那会累积订阅 ✗）
    /// </summary>
    private static (Storyboard board, DoubleAnimation x, DoubleAnimation y) CreateScaleStoryboard(Transform transform, float to)
    {
        var x = new DoubleAnimation
        {
            To = to,
            Duration = Duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var y = new DoubleAnimation
        {
            To = to,
            Duration = Duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(x, transform);
        Storyboard.SetTargetProperty(x, "ScaleX");
        Storyboard.SetTarget(y, transform);
        Storyboard.SetTargetProperty(y, "ScaleY");

        var board = new Storyboard();
        board.Children.Add(x);
        board.Children.Add(y);

        if (Trace)
            board.Completed += (_, _) => Log($"放大完成：To={x.To:F3} 变换 {transform.GetType().FullName}");

        return (board, x, y);
    }

    private static bool ReadAnimationsEnabled()
    {
        try { return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled; }
        catch { return true; }
    }

    private static void Log(string message) => OSTGUI.Services.LogService.Diag($"[Hover] {message}");
}
