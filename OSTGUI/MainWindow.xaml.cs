using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using OSTGUI.Pages;
using OSTGUI.Services;
using OSTGUI.ViewModels;
using System.Runtime.InteropServices;
using System.Reflection;

namespace OSTGUI;

public sealed partial class MainWindow : Microsoft.UI.Xaml.Window
{
    private readonly MainViewModel _mainVM;
    private readonly Dictionary<string, Page> _pageCache = new();

    public MainWindow()
    {
        this.InitializeComponent();

        _mainVM = App.Services.GetRequiredService<MainViewModel>();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // 注册窗口状态保存/恢复（位置）
        WindowStateSaver.WinUi3.WindowStateSaver.RegisterAndLoad(this);

        // 配置已在窗口创建前完整加载，直接应用侧边栏/窗口状态，避免启动闪烁
        ApplyWindowStateFromConfig();

        // 确保窗口在屏幕可见区域内
        EnsureWindowIsVisible();

        // 监听标题变化
        _mainVM.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Title))
                AppTitleBarText.Text = _mainVM.Title;
        };

        // 设置初始标题
        AppTitleBarText.Text = _mainVM.Title;

        this.Activated += OnWindowActivated;
        this.Closed += OnClosed;
    }

    /// <summary>
    /// 从已加载的配置中应用侧边栏展开状态、宽度与窗口大小
    /// </summary>
    private void ApplyWindowStateFromConfig()
    {
        try
        {
            var config = _mainVM.ConfigService.Config;

            MainNavView.IsPaneOpen = config.IsNavigationPaneOpen;
            // 侧边栏宽度可配置（配置文件 NavigationPaneWidth，默认 300）
            if (config.NavigationPaneWidth >= 200 && config.NavigationPaneWidth <= 600)
                MainNavView.OpenPaneLength = config.NavigationPaneWidth;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero) return;

            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            if (appWindow == null) return;

            // 全局窗口最小尺寸（系统级锁定，WM_GETMINMAXINFO）
            if (_hwnd == IntPtr.Zero)
                SetupMinTrackSize(hwnd);

            if (config.WindowWidth > 400 && config.WindowHeight > 300)
            {
                appWindow.Resize(new Windows.Graphics.SizeInt32(
                    Math.Max((int)config.WindowWidth, MinWindowWidth),
                    Math.Max((int)config.WindowHeight, MinWindowHeight)));
            }
        }
        catch { }
    }

    private const int MinWindowWidth = 800;
    private const int MinWindowHeight = 560;
    private const int GwlWndProc = -4;
    private const uint WmGetMinMaxInfo = 0x0024;

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

    private void EnsureWindowIsVisible()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero) return;

            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            if (appWindow == null) return;

            // 如果位置在屏幕外，重置到屏幕中心
            if (appWindow.Position.X < 0 || appWindow.Position.Y < 0)
            {
                appWindow.Resize(new Windows.Graphics.SizeInt32(1250, 875));
                appWindow.Move(new Windows.Graphics.PointInt32(100, 100));
            }
        }
        catch { }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _mainVM.StopSteamStatusPolling();
        _mainVM.StopLibraryRefreshTimer();
        SaveSizeToConfig();
    }

    private void SaveSizeToConfig()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero) return;

            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            if (appWindow == null) return;

            var config = _mainVM.ConfigService.Config;
            config.WindowWidth = appWindow.Size.Width;
            config.WindowHeight = appWindow.Size.Height;
            _mainVM.ConfigService.SaveAsync().GetAwaiter().GetResult();
        }
        catch { }
    }

    public NavigationView NavView => MainNavView;
    public Grid Root => RootGrid;

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!_initialized)
        {
            _initialized = true;
            _ = InitializeAppAsync();
        }

        this.Activated -= OnWindowActivated;
    }

    private bool _initialized;

    private async Task InitializeAppAsync()
    {
        try
        {
            await _mainVM.InitializeAsync(DispatcherQueue);

            _mainVM.SettingsVM.LoadFromConfig();

            var config = _mainVM.ConfigService.Config;
            NavigateTo(config.DefaultPage == "search" ? "search" : "home");

            ApplyThemeAndChrome();

            // 配置加载完成后重新读取入库选项，
            // 避免启动瞬间 ViewModel 用默认值初始化后覆盖真实配置
            _mainVM.SearchVM.LoadOptionsFromConfig();

            // 刷新库统计并更新标题
            await _mainVM.RefreshLibraryStatsAsync();
        }
        catch { }
    }

    /// <summary>
    /// 应用主题并同步窗口级外观：
    /// XAML 主题、标题栏按钮颜色、转换器深色标志、刷新库列表（触发绑定重算）
    /// </summary>
    public void ApplyThemeAndChrome()
    {
        try
        {
            var theme = _mainVM.SettingsVM.ThemeMode switch
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
            RefreshLibraryIfLoaded();
        }
        catch { }
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
    /// 库页面已加载时强制刷新列表，使前景色转换器按新主题重新求值
    /// </summary>
    private void RefreshLibraryIfLoaded()
    {
        try
        {
            if (_pageCache.TryGetValue("library", out var page) && page is Pages.LibraryPage libPage)
            {
                _ = libPage.VM.LoadLibraryCommand.ExecuteAsync(null);
            }
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
            NavigateTo(tag);
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

    private async void NavInfo_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = $"版本 v{version}", FontSize = 14 });
        content.Children.Add(new TextBlock { Text = "制作者 ZJY", FontSize = 14 });
        content.Children.Add(new TextBlock
        {
            Text = "https://github.com/hazjy/OSTGUI",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        });

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "关于 OSTGUI",
            Content = content,
            PrimaryButtonText = "GitHub",
            CloseButtonText = "退出",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://github.com/hazjy/OSTGUI",
                    UseShellExecute = true
                });
            }
            catch { }
        };

        await dialog.ShowAsync();
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

        var panel = new StackPanel { Spacing = 8 };
        var scroll = new ScrollViewer
        {
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = panel
        };

        var cardBrush = Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Microsoft.UI.Xaml.Media.Brush;
        var secondaryBrush = Application.Current.Resources["TextFillColorSecondaryBrush"] as Microsoft.UI.Xaml.Media.Brush;

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
                BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Microsoft.UI.Xaml.Media.Brush,
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
            var accountName = acc.AccountName;
            btn.Click += async (s, args) =>
            {
                dialog.Hide();
                await RestartToAccountAsync(accountName);
            };
            panel.Children.Add(btn);
        }

        dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "选择要登录的账号",
            Content = scroll,
            CloseButtonText = "取消"
        };

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

    private void NavigateTo(string pageTag)
    {
        Page? page = null;

        if (_pageCache.TryGetValue(pageTag, out var cached))
        {
            page = cached;
        }
        else
        {
            page = pageTag switch
            {
                "home" => new HomePage(_mainVM),
                "search" => new SearchPage(_mainVM.SearchVM),
                "library" => new LibraryPage(_mainVM.LibraryVM),
                "online" => new OnlinePage(_mainVM.OnlineVM),
                "denuvo" => new DenuvoPage(_mainVM.DenuvoVM),
                "settings" => new SettingsPage(_mainVM.SettingsVM),
                _ => new HomePage(_mainVM),
            };

            _pageCache[pageTag] = page;
        }

        ContentFrame.Content = page;
    }
}
