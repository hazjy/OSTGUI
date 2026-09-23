using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Extensions.DependencyInjection;
using OSTGUI.Pages;
using OSTGUI.Services;
using OSTGUI.ViewModels;
using System.Runtime.InteropServices;

namespace OSTGUI;

public sealed partial class MainWindow : Microsoft.UI.Xaml.Window
{
    private readonly MainViewModel _mainVM;
    // 页面实例复用交给 Frame 的原生缓存（各页 XAML 里 NavigationCacheMode="Enabled"），
    // 不再自己维护一份 Dictionary<string, Page>（那套还会绕过 Frame 的导航过渡）

    public MainWindow()
    {
        this.InitializeComponent();

        _mainVM = App.Services.GetRequiredService<MainViewModel>();

        // ⚠️ 主题必须赶在**窗口显示之前**落地（App.OnLaunched 紧接着就 Activate()）：
        //    - 晚一步，第一帧还是按系统主题画的 —— 系统深色 + 应用浅色时，用户看到"顶上闪一下"
        //    - 首屏页面也会在旧主题下被建出来，它里面的 {ThemeResource} 就可能停在那一套
        //    这里直接读配置而不是 SettingsVM：VM 要等 InitializeAppAsync 里的 LoadFromConfig() 才准，
        //    在那之前它只会给出默认的 "auto"。
        ApplyThemeAndChrome(refreshLibrary: false, themeMode: _mainVM.ConfigService.Config.ThemeMode);

        // 点击空白（非输入控件区域）时把焦点收回到全局锚点，统一取消各页面输入框激活。
        // WinUI 在"已有焦点"时不会自行转移焦点（microsoft-ui-xaml #10051），需主动拉取；
        // TryEnqueue 保证在本轮指针事件的框架焦点处理之后执行，同时覆盖 #4364 的空点击误聚焦。
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Root_PointerPressed), true);

        ExtendsContentIntoTitleBar = true;
        // 拖拽区 = 顶部那条 48px（2026-09-23 试过 12px / 整窗拖拽，都不如这条好看 → 已恢复）
        SetTitleBar(AppTitleBar);

        // ⚠️ 内容延伸到标题栏后，系统仍按 caption 的颜色画顶上那一条。这里把"背景"设透明，
        // 让标题栏区域跟窗口其余部分一样透出亚克力/云母；hover / pressed 保持系统默认，
        // 否则鼠标移到最小化/最大化/关闭上会没有反馈
        var titleBar = AppWindow.TitleBar;
        titleBar.BackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.InactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

        // 显示效果（亚克力/云母）要排在 caption 色设好**之后**：顺序反了，第一帧顶上会先露出一条
        // 系统 caption 色再变成透明。「无」模式那层纯色底的可见性在 ApplyBackdrop 里一并设好。
        ApplyBackdrop(_mainVM.ConfigService.Config.BackdropMode);

        // 侧边栏是"平级切换"，没有返回语义：关掉导航栈，免得每切一次都往 BackStack 里塞一个（见 GoToPage）
        ContentFrame.IsNavigationStackEnabled = false;

        // 配置已在窗口创建前完整加载，直接应用侧边栏/窗口状态，避免启动闪烁
        ApplyWindowStateFromConfig();

        // 窗口激活时初始化应用
        this.Activated += OnWindowActivated;
        // 窗口关闭时清理临时资源
        this.Closed += OnClosed;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _mainVM.StopSteamStatusPolling();
        SaveSizeToConfig();

        // 清理 NoSteamLauncher 临时资源
        try
        {
            var noSteamService = App.Services.GetService<NoSteamLauncherService>();
            if (noSteamService is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch { }
    }

    /// <summary>
    /// 从已加载的配置中应用侧边栏展开状态、宽度与窗口大小/位置。
    /// 尺寸与位置统一走 Win32（本进程 PerMonitorV2 → 物理像素），存的是
    /// <c>GetWindowPlacement().rcNormalPosition</c>（**还原态**矩形，最小化/最大化关闭时也正确）。
    /// ⚠️ 这里**只做尺寸/位置**，不要调 <c>Maximize()</c> 这类会显示/激活窗口的 API —— 构造函数阶段
    /// 提前激活会让后面注册的 <c>Activated</c> 订阅永久丢失（见 <see cref="ApplyStartupMaximizeIfNeeded"/>）。
    /// </summary>
    private void ApplyWindowStateFromConfig()
    {
        try
        {
            var config = _mainVM.ConfigService.Config;

            MainNavView.IsPaneOpen = config.IsNavigationPaneOpen;
            // 侧边栏宽度可配置（配置文件 NavigationPaneWidth，默认 360）
            if (config.NavigationPaneWidth >= 150 && config.NavigationPaneWidth <= 600)
                MainNavView.OpenPaneLength = config.NavigationPaneWidth;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero) return;

            // 全局窗口最小尺寸（系统级锁定，WM_GETMINMAXINFO）
            if (_hwnd == IntPtr.Zero)
                SetupMinTrackSize(hwnd);

            // 最小尺寸常量是逻辑值（MinMaxInfo 里按 DPI 放大），这里要按物理像素比较
            var dpi = GetDpiForWindow(hwnd);
            var work = GetWorkArea(hwnd);
            var maxWidth = Math.Max(work.Right - work.Left, ScaleLogical(MinWindowWidth, dpi));
            var maxHeight = Math.Max(work.Bottom - work.Top, ScaleLogical(MinWindowHeight, dpi));
            var width = Math.Min(Math.Max((int)config.WindowWidth, ScaleLogical(MinWindowWidth, dpi)), maxWidth);
            var height = Math.Min(Math.Max((int)config.WindowHeight, ScaleLogical(MinWindowHeight, dpi)), maxHeight);

            // 位置：旧配置是 -1（从未存过）或落在已拔掉的显示器上 → 保持系统默认位置
            var hasPosition = config.WindowX >= 0 && config.WindowY >= 0
                && IsPointOnScreen((int)config.WindowX, (int)config.WindowY);

            SetWindowPos(hwnd, IntPtr.Zero,
                hasPosition ? (int)config.WindowX : 0,
                hasPosition ? (int)config.WindowY : 0,
                width, height,
                SwpNoZOrder | SwpNoActivate | (hasPosition ? 0u : SwpNoMove));
        }
        catch { }
    }

    /// <summary>
    /// 恢复"上次是最大化关闭"的状态。**必须在窗口已激活之后调用**（<c>App.OnLaunched</c> 里
    /// <c>Activate()</c> 之后）：<c>OverlappedPresenter.Maximize()</c> 对未显示的窗口等价于
    /// <c>ShowWindow(SW_MAXIMIZE)</c>，会当场激活窗口并同步抛出 <c>Activated</c>；若此时构造函数
    /// 里的订阅还没注册，那个事件就永久丢失，初始化随之永不执行（实测症状：主页空白、
    /// Steam 路径/DLL 全部为空）。窗口已激活后再最大化不会改变激活状态，因此安全。
    /// </summary>
    public void ApplyStartupMaximizeIfNeeded()
    {
        try
        {
            if (!_mainVM.ConfigService.Config.IsWindowMaximized) return;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero) return;

            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            if (Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId)?.Presenter
                is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                presenter.Maximize();
        }
        catch { }
    }

    /// <summary>
    /// 运行时应用侧边栏宽度（立即生效，不动窗口尺寸；重启后由 ApplyWindowStateFromConfig 复用同一值）
    /// </summary>
    public void ApplyNavigationPaneWidth(double width)
    {
        if (width >= 150 && width <= 600)
            MainNavView.OpenPaneLength = width;
    }

    private const int MinWindowWidth = 800;
    private const int MinWindowHeight = 560;
    private const int GwlWndProc = -4;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint MonitorDefaultToNearest = 0x00000002;

    private IntPtr _hwnd;
    private IntPtr _oldWndProc;
    private WndProcDelegate? _wndProcHook;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint PtReserved;
        public NativePoint PtMaxSize;
        public NativePoint PtMaxPosition;
        public NativePoint PtMinTrackSize;
        public NativePoint PtMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public Rect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    /// <summary>
    /// 通过替换窗口过程设置系统级最小尺寸，拖动窗口到边界即锁定，无拉回动画
    /// </summary>
    private void SetupMinTrackSize(IntPtr hwnd)
    {
        try
        {
            _hwnd = hwnd;
            _wndProcHook = WndProcHook;
            _oldWndProc = SetWindowLongPtr(
                hwnd, GwlWndProc, Marshal.GetFunctionPointerForDelegate(_wndProcHook));
        }
        catch { }
    }

    private IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WmGetMinMaxInfo)
            {
                var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                var dpi = GetDpiForWindow(hWnd);
                mmi.PtMinTrackSize.X = ScaleLogical(MinWindowWidth, dpi);
                mmi.PtMinTrackSize.Y = ScaleLogical(MinWindowHeight, dpi);
                Marshal.StructureToPtr(mmi, lParam, false);
            }
        }
        catch { }

        return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    private static int ScaleLogical(int logical, uint dpi)
        => (int)Math.Round(logical * dpi / 96.0);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    // ── 窗口状态持久化用的 Win32（本进程 PerMonitorV2 → 这里的数值都是物理像素）──

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>当前显示器工作区（物理像素）；取不到时返回一个"不设上限"的矩形，即不做夹取</summary>
    private static Rect GetWorkArea(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            return info.Work;

        return new Rect { Left = 0, Top = 0, Right = int.MaxValue, Bottom = int.MaxValue };
    }

    private static bool IsPointOnScreen(int x, int y)
        => MonitorFromPoint(new NativePoint { X = x, Y = y }, 0) != IntPtr.Zero;

    /// <summary>
    /// 保存窗口状态。用 <c>GetWindowPlacement().rcNormalPosition</c> —— 它永远是**还原态**矩形，
    /// 最小化/最大化关闭时也能拿到正确尺寸（旧实现取 <c>AppWindow.Size</c>，最小化存 353x56、
    /// 最大化存 3868x2080，下次启动就错乱）。
    /// </summary>
    private void SaveSizeToConfig()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero) return;

            var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
            if (!GetWindowPlacement(hwnd, ref placement)) return;

            var rect = placement.NormalPosition;
            if (rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0) return;

            var config = _mainVM.ConfigService.Config;
            config.WindowWidth = rect.Right - rect.Left;
            config.WindowHeight = rect.Bottom - rect.Top;
            config.WindowX = rect.Left;
            config.WindowY = rect.Top;
            config.IsWindowMaximized = IsZoomed(hwnd);
            _mainVM.ConfigService.SaveAsync().GetAwaiter().GetResult();
        }
        catch { }
    }

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsInteractive(e.OriginalSource as DependencyObject)) return;
        _ = DispatcherQueue?.TryEnqueue(() => GlobalFocusAnchor.Focus(FocusState.Programmatic));
    }

    /// <summary>交互控件（及模板内部）不干预；仅空白点击才回收焦点。
    /// ponytail: 白名单按页面现有控件类型枚举；未来新增交互控件（如 Pivot/TabView/FlipView）时需补类型，否则点击它们会被当作空白抢焦点
    /// </summary>
    private static bool IsInteractive(DependencyObject? el)
    {
        while (el != null)
        {
            if (el is TextBox or PasswordBox or NumberBox or AutoSuggestBox
                or ComboBox or Slider or ToggleSwitch or ListViewBase
                or Microsoft.UI.Xaml.Controls.Primitives.ScrollBar
                or Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)
                return true;
            el = VisualTreeHelper.GetParent(el);
        }
        return false;
    }

    public NavigationView NavView => MainNavView;
    public Grid Root => RootGrid;

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        EnsureInitialized();
        this.Activated -= OnWindowActivated;
    }

    private bool _initialized;

    /// <summary>
    /// 幂等的初始化入口。**不要只依赖一次性的 <c>Activated</c> 事件**：窗口在构造阶段被提前激活
    /// （例如恢复"最大化"）时那个事件可能已经抛过、订阅却还没注册，初始化就会静默丢失
    /// （症状：内容区空白、Steam 路径/DLL 为空、状态轮询与库刷新定时器都没启动）。
    /// 因此 <c>App.OnLaunched</c> 在 <c>Activate()</c> 之后也直接调一次本方法兜底。
    /// </summary>
    public void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        _ = InitializeAppAsync();
    }

    private async Task InitializeAppAsync()
    {
        try
        {
            await _mainVM.InitializeAsync(DispatcherQueue);

            _mainVM.SettingsVM.LoadFromConfig();

            var config = _mainVM.ConfigService.Config;

            // 主题先于建页面落地：页面在旧主题下建出来，里面的 {ThemeResource} 可能就停在那一套
            ApplyThemeAndChrome();

            var page = config.DefaultPage == "search" ? "search" : "home";
            GoToPage(page);

            // 配置加载完成后重新读取入库选项，
            // 避免启动瞬间 ViewModel 用默认值初始化后覆盖真实配置
            _mainVM.SearchVM.LoadOptionsFromConfig();

            // 排查"启动后空白/数据全空"时先看这一行在不在（初始化有没有跑完）
            LogService.AddAppLog($"[Init] page={page}, steam={_mainVM.SteamPathDisplay}");
        }
        catch (Exception ex)
        {
            // 以前这里静默吞掉，导致"初始化没跑"和"初始化跑了但失败"完全无法区分
            LogService.AddAppLog($"[Init] 初始化失败: {ex}");
        }
    }

    /// <summary>
    /// 应用主题并同步窗口级外观：
    /// XAML 主题、标题栏按钮颜色、转换器深色标志、刷新库列表（触发绑定重算）
    /// </summary>
    /// <param name="refreshLibrary">
    /// 构造阶段必须传 false：库数据还没加载，跑一遍刷新命令等于提前触发一次扫描
    /// </param>
    /// <param name="themeMode">
    /// 主题取值来源，缺省读 <c>SettingsVM</c>；构造阶段必须显式传配置里的值 —— 那时 VM 还没
    /// <c>LoadFromConfig()</c>，读 VM 只会拿到默认的 "auto"（跟随系统主题）
    /// </param>
    public void ApplyThemeAndChrome(bool refreshLibrary = true, string? themeMode = null)
    {
        try
        {
            var theme = (themeMode ?? _mainVM.SettingsVM.ThemeMode) switch
            {
                "dark" => ElementTheme.Dark,
                "light" => ElementTheme.Light,
                _ => ElementTheme.Default
            };
            RootGrid.RequestedTheme = theme;

            var isDark = theme switch
            {
                ElementTheme.Light => false,
                ElementTheme.Dark => true,
                _ => Application.Current.RequestedTheme == ApplicationTheme.Dark
            };

            Helpers.ThemeColorHelper.IsDarkTheme = isDark;
            UpdateTitleBarButtonColor(isDark);
            if (refreshLibrary) RefreshLibraryIfLoaded();
        }
        catch { }
    }

    /// <summary>
    /// 显示效果：无 / 云母 / 亚克力（设置页改完即时调用；不支持的系统由框架回落纯色底）
    /// </summary>
    public void ApplyBackdrop(string mode)
    {
        SystemBackdrop = mode switch
        {
            "none" => null,
            "acrylic" => new DesktopAcrylicBackdrop(),
            _ => new MicaBackdrop(),
        };

        // 「无」时窗口底色跟的是系统主题，会和应用内主题打架，铺一层自己的纯色底（见 XAML 注释）
        SolidBackdrop.Visibility = SystemBackdrop is null ? Visibility.Visible : Visibility.Collapsed;

        // 选了没效果时先看这行日志在不在、类名对不对
        LogService.AddAppLog($"[Backdrop] {mode} -> {SystemBackdrop?.GetType().Name ?? "null"}");
    }

    /// <summary>
    /// 同步标题栏控制按钮颜色（应用内主题切换不影响系统默认按钮，需显式设置）
    /// </summary>
    private void UpdateTitleBarButtonColor(bool isDark)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

            appWindow.TitleBar.ButtonForegroundColor = isDark
                ? Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(255, 0x1B, 0x1B, 0x1B);
        }
        catch { }
    }

    /// <summary>
    /// 库页面已加载时强制刷新列表，使前景色转换器按新主题重新求值。
    /// 直接对 VM 发命令：页面实例现在由 Frame 的 `NavigationCacheMode` 缓存，拿不到也不该去掏页面
    /// </summary>
    private void RefreshLibraryIfLoaded()
    {
        try
        {
            _ = _mainVM.LibraryVM.LoadLibraryCommand.ExecuteAsync(null);
        }
        catch { }
    }

    private void MainNavView_PaneOpening(object? sender, object e)
    {
        try { _mainVM.ConfigService.Config.IsNavigationPaneOpen = true; _mainVM.ConfigService.SaveAsync().GetAwaiter().GetResult(); } catch { }
    }

    private void MainNavView_PaneClosing(object? sender, object e)
    {
        try { _mainVM.ConfigService.Config.IsNavigationPaneOpen = false; _mainVM.ConfigService.SaveAsync().GetAwaiter().GetResult(); } catch { }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
            return;

        if (!string.IsNullOrEmpty(tag))
        {
            GoToPage(tag);
        }
    }

    private void MainNavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        // 每次点击设置项（含重复点击当前项）都触发齿轮旋转
        if (args.InvokedItemContainer == NavSettings)
            RotateIcon(SettingsGlyphIcon);
    }

    private void NavRestartSteam_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is not NavigationViewItem item) return;
        RotateIcon(RestartGlyphIcon);

        var menu = (MenuFlyout)RootGrid.Resources["RestartSteamMenu"];
        // 弹窗右移，避免遮挡左侧图标的旋转动效
        menu.ShowAt(item, new Windows.Foundation.Point(40, item.ActualHeight));
    }

    /// <summary>
    /// 图标点击旋转动效（Composition 动画，0 → 360 度）
    /// </summary>
    private void RotateIcon(Microsoft.UI.Xaml.Controls.FontIcon icon)
    {
        try
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(icon);
            var compositor = visual.Compositor;
            var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0f, 0f);
            animation.InsertKeyFrame(1f, 360f);
            animation.Duration = TimeSpan.FromMilliseconds(450);
            animation.Target = "RotationAngleInDegrees";

            var width = icon.ActualWidth > 0 ? icon.ActualWidth : 16;
            var height = icon.ActualHeight > 0 ? icon.ActualHeight : 16;
            visual.CenterPoint = new System.Numerics.Vector3((float)width / 2, (float)height / 2, 0f);
            visual.StartAnimation("RotationAngleInDegrees", animation);
        }
        catch { }
    }

    private async void RestartSteam_Click(object sender, RoutedEventArgs e)
    {
        var steamService = App.Services.GetRequiredService<SteamService>();
        var (success, message) = await steamService.RestartSteamAsync();

        if (success)
            Services.ToastService.ShowSuccess("重启 Steam", message);
        else
            Services.ToastService.ShowError("重启 Steam 失败", message);
    }

    private async void RestartSteamAccount_Click(object sender, RoutedEventArgs e)
    {
        var steamService = App.Services.GetRequiredService<SteamService>();
        var accounts = steamService.GetSteamAccounts();
        if (accounts.Count == 0)
        {
            Services.ToastService.ShowInfo("未找到账号", "本地没有记住的 Steam 账号");
            return;
        }

        // 画刷从 XAML 取样点读（{ThemeResource} 按应用主题解析）。
        // 不能再用 Application.Current.Resources[...]：那个走系统主题，浅色应用 + 深色系统会把弹窗染成深色。
        var secondaryBrush = ProbeSecondary.Foreground;
        var cardBrush = ProbeCard.Background;
        var strokeBrush = ProbeStroke.BorderBrush;
        var accentBrush = ProbeAccent.Background;

        var panel = new StackPanel { Spacing = 8 };
        var scroll = new ScrollViewer
        {
            MaxHeight = 280,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = panel
        };

        // 账户卡片：点击仅做选中（高亮描边），不直接触发重启
        string? selected = null;
        var cards = new List<(Button Btn, string AccountName)>();
        Button confirmBtn = null!;
        ContentDialog dialog = null!;

        foreach (var acc in accounts)
        {
            var display = string.IsNullOrEmpty(acc.PersonaName) ? acc.AccountName : acc.PersonaName;
            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(14, 10, 14, 10),
                CornerRadius = new CornerRadius(6),
                Background = cardBrush,
                BorderThickness = new Thickness(1),
                BorderBrush = strokeBrush,
                Content = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = display, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                        new TextBlock { Text = acc.AccountName, FontSize = 11, Foreground = secondaryBrush }
                    }
                }
            };

            var captured = btn;
            var accountName = acc.AccountName;
            btn.Click += (s, args) =>
            {
                selected = accountName;
                foreach (var (b, _) in cards)
                {
                    var isSelected = ReferenceEquals(b, captured);
                    b.BorderBrush = isSelected ? accentBrush : strokeBrush;
                    b.BorderThickness = new Thickness(isSelected ? 2 : 1);
                }
                // 选中即用框架自带的强调按钮样式（填充/前景色都由框架按当前主题处理）
                confirmBtn.Style = (Microsoft.UI.Xaml.Style)Application.Current.Resources["AccentButtonStyle"];
                confirmBtn.IsEnabled = true;
            };

            cards.Add((captured, accountName));
            panel.Children.Add(btn);
        }

        var hint = new TextBlock
        {
            Text = "选中账户后，点击“确认重启”",
            FontSize = 12,
            Foreground = secondaryBrush,
        };

        // 底部按钮行：确认按钮此刻仍为框架默认灰色，选中账户后才现场染为主题蓝
        confirmBtn = new Button
        {
            Content = "确认重启",
            Padding = new Thickness(18, 8, 18, 8),
            CornerRadius = new CornerRadius(6),
            IsEnabled = false,
        };
        confirmBtn.Click += async (s, args) =>
        {
            if (string.IsNullOrEmpty(selected)) return;
            var name = selected;
            dialog.Hide();
            await RestartToAccountAsync(name);
        };

        var cancelBtn = new Button
        {
            Content = "取消",
            Padding = new Thickness(18, 8, 18, 8),
            CornerRadius = new CornerRadius(6),
        };
        cancelBtn.Click += (s, args) => dialog.Hide();

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        buttonRow.Children.Add(confirmBtn);
        buttonRow.Children.Add(cancelBtn);

        var rootPanel = new StackPanel { Spacing = 10 };
        rootPanel.Children.Add(hint);
        rootPanel.Children.Add(scroll);
        rootPanel.Children.Add(buttonRow);

        dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "选择要登录的账号",
            Content = rootPanel
            // 不显式设 RequestedTheme：2026-09-20 实测（Windows 深色 + 应用浅色）弹层**本来就跟随应用主题**
            // （弹窗 浅 #E7E7E7 / 深 #393939，菜单 浅 #F7F7F7 / 深 #362C2F），显式再设一遍数值完全一致，
            // 所以按"走框架默认"的原则不再干预。旧注释「弹窗不继承主题、刻意保留深色」与实测不符，已作废。
        };
        dialog.PrimaryButtonClick += (s, args) =>
        {
            if (!string.IsNullOrEmpty(selected))
                _ = RestartToAccountAsync(selected);
        };

        Helpers.PopupTheme.Apply(dialog);
        await dialog.ShowAsync();
    }

    private async Task RestartToAccountAsync(string accountName)
    {
        var steamService = App.Services.GetRequiredService<SteamService>();
        var (success, message) = await steamService.RestartSteamAsync(accountName);

        if (success)
            Services.ToastService.ShowSuccess("重启 Steam", message);
        else
            Services.ToastService.ShowError("重启 Steam 失败", message);
    }

    /// <summary>
    /// 切页统一入口（公开：主页的快捷操作也走这里）。**必须走 `Frame.Navigate`**：`Frame` 会自动播
    /// `NavigationThemeTransition`，默认动画就是 page refresh（内容上浮 + 淡入）；早先用
    /// `ContentFrame.Content = page` 直接换内容会绕过它，于是页面上什么进场动画都没有（2026-09-23 的结论）。
    /// 页面实例复用改由各页 XAML 的 `NavigationCacheMode="Enabled"` 负责（等价于早先那个手写字典）。
    /// </summary>
    public void GoToPage(string pageTag)
    {
        var pageType = pageTag switch
        {
            "search" => typeof(Pages.SearchPage),
            "library" => typeof(Pages.LibraryPage),
            "online" => typeof(Pages.OnlinePage),
            "denuvo" => typeof(Pages.DenuvoPage),
            "nosteam" => typeof(Pages.NoSteamPage),
            "settings" => typeof(Pages.SettingsPage),
            "info" => typeof(Pages.InfoPage),
            _ => typeof(Pages.HomePage),
        };

        ContentFrame.Navigate(pageType, null, new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo());
    }
}
