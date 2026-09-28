using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OSTGUI.Models;
using OSTGUI.Services;
using OSTGUI.ViewModels;
using Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;


namespace OSTGUI.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel VM { get; }

    /// <summary>无参构造：`Frame.Navigate` 需要它</summary>
    public SettingsPage() : this(App.Services.GetRequiredService<MainViewModel>().SettingsVM) { }

    public SettingsPage(SettingsViewModel vm)
    {
        this.InitializeComponent();
        VM = vm;
        this.DataContext = VM;

        // 日志栏刷新的节流表（100ms = 最多 10 次/秒 ✗）：在 UI 线程上建 ✓，有变化才启动、刷完就停 ✓。
        // ⚠️ 必须建在下面两个 lambda **之前**：那两处会读它（字段是非空 readonly ✓，
        //    编译器对"lambda 先建、字段后赋"会报 CS8602 可能空引用）
        _logRefreshTimer = DispatcherQueue.CreateTimer();
        _logRefreshTimer.Interval = TimeSpan.FromMilliseconds(100);
        _logRefreshTimer.IsRepeating = true;
        _logRefreshTimer.Tick += (_, _) => FlushLogsText();

        // ── 页面级订阅一律走 Loaded/Unloaded 成对（2026-09-27）──────────────────────────
        // 这几个事件源是**单例/静态**的（`LogService.Logs` 是静态集合、VM 是单例）✗：
        // 在构造函数里订阅 = 静态对象把**页面实例**永久钉住 ✗（页面再也回收不掉 ✓）。
        // 而页面被 Frame 缓存（`NavigationCacheMode="Enabled"`）会**反复 Loaded** ✗
        // → 必须"先 -= 再 +="，否则每进一次页面就多订一份 ✓
        Loaded += (s, e) =>
        {
            VM.ThemeChanged -= OnThemeChanged;
            VM.ThemeChanged += OnThemeChanged;
            VM.BackdropChanged -= OnBackdropChanged;
            VM.BackdropChanged += OnBackdropChanged;
            // 监听日志变更，自动更新 LogsText 并滚动到底部。
            // 必须全面防御：LogService.AddLog 可能来自任意线程（经 DispatcherQueue 封送），
            // 本处理器抛出的异常会反向炸进日志调用方，掩盖真实错误
            LogService.Logs.CollectionChanged -= OnLogsChanged;
            LogService.Logs.CollectionChanged += OnLogsChanged;

            // 每次进入页面同步一次当前日志：仅在无新日志事件时，日志栏不依赖事件也有内容
            VM.LogsText = LogService.GetText(PanelLines);
            VM.RefreshSudamaCacheAge();
            VM.RefreshPaths();
            VM.RefreshDenuvoModeFromKernel();
        };

        Unloaded += (s, e) =>
        {
            VM.ThemeChanged -= OnThemeChanged;
            VM.BackdropChanged -= OnBackdropChanged;
            LogService.Logs.CollectionChanged -= OnLogsChanged;
            _logRefreshTimer.Stop();     // 离开页面就不再重算日志栏（重算代价固定，但没必要付 ✗）
        };
    }

    /// <summary>
    /// 日志栏最多显示多少行。只影响面板显示：集合里仍是全量，"复制全部"也走全量。
    /// 面板本身只有 400px 高，显示尾部足够看当下；要看更早的翻日志文件。
    /// </summary>
    private const int PanelLines = 2000;

    /// <summary>日志栏刷新表（见构造里的说明）</summary>
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _logRefreshTimer;

    /// <summary>有未刷新的日志变更（跨线程只写这个 bool ✓ 有意的良性竞态：最差多排一次 UI 回调）</summary>
    private volatile bool _logDirty;

    /// <summary>
    /// 日志变更（可能在**任意线程**）：只置脏 + 叫醒节流表，真正重算交给 <see cref="FlushLogsText"/>。
    /// 原来这里每来一行就 `string.Join` 全量重拼（几千行就是几十万字符 ✗ 每次都进 LOH ✗）
    /// 再刷 TextBox + Select 到底 → 日志一多就疯狂分配（2026-09-27 修成最多 10 次/秒；
    /// 2026-09-28 再加面板尾部上限 PanelLines，重算量不再随总量增长）
    /// </summary>
    private void OnLogsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        try
        {
            if (_logDirty) return;      // 一次突发只排一次 UI 回调 ✓
            _logDirty = true;
            var dq = DispatcherQueue;   // 表只能在 UI 线程上启动 ✓
            if (dq == null) return;
            _ = dq.TryEnqueue(() =>
            {
                if (_logRefreshTimer.IsRunning == false) _logRefreshTimer.Start();
            });
        }
        catch { }
    }

    /// <summary>节流表的 Tick（UI 线程）：有脏才重算一次，没脏就停表 ✓</summary>
    private void FlushLogsText()
    {
        _logDirty = false;

        try
        {
            VM.LogsText = LogService.GetText(PanelLines);
        }
        catch { }

        try
        {
            if (LogsTextBox != null && LogsTextBox.Text != null)
            {
                LogsTextBox.Select(LogsTextBox.Text.Length, 0);
            }
        }
        catch { }

        // 刷的这 100ms 里若又来了日志，_logDirty 已被重新置起 ✓ → 不停表，下一拍继续 ✓
        if (_logDirty == false) _logRefreshTimer.Stop();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (App.MainWindow is MainWindow mw)
        {
            mw.ApplyThemeAndChrome();
        }
    }

    private void OnBackdropChanged(object? sender, EventArgs e)
    {
        if (App.MainWindow is MainWindow mw)
        {
            mw.ApplyBackdrop(VM.BackdropMode);
        }
    }

    private void ApplyNavigationPaneWidth_Click(object sender, RoutedEventArgs e)
    {
        VM.ApplyNavigationPaneWidthCommand.Execute(null);
        if (App.MainWindow is MainWindow mw && int.TryParse(VM.NavigationPaneWidthInput.Trim(), out var w))
        {
            mw.ApplyNavigationPaneWidth(w);
            VM.NavigationPaneWidthInput = "";                    // 清空输入框
            (sender as Button)?.Focus(FocusState.Programmatic);  // 焦点移出输入框（按钮状态随属性通知自动回灰）
        }
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        // 所有设置实时保存
        VM.SaveAllToConfig();
    }

    /// <summary>
    /// API Key / Token 密码框：仅在实际值变化时记录"上次更新"（聚焦记旧值，失焦比对）
    /// </summary>
    private ManifestSource? _tokenFocusSource;
    private string? _tokenFocusOldValue;

    private void OnTokenGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: ManifestSource ms } && ms.RequiresToken)
        {
            _tokenFocusSource = ms;
            _tokenFocusOldValue = ms.ApiKey;
        }
    }

    private void OnTokenLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb && _tokenFocusSource != null && ReferenceEquals(pb.DataContext, _tokenFocusSource))
        {
            var newValue = pb.Password;
            if (newValue != _tokenFocusOldValue)
            {
                _tokenFocusSource.ApiKey = newValue;
                VM.MarkManifestKeyUpdated(_tokenFocusSource);
                VM.SaveAllToConfig();
            }
        }
        _tokenFocusSource = null;
        _tokenFocusOldValue = null;
    }

    /// <summary>Lua 路径：失焦时把输入框里的目录写进内核配置</summary>
    private void OnLuaPathLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) VM.LuaPath = tb.Text ?? string.Empty;
        VM.SyncLuaPathToKernel();
    }

    private async void BrowseLua_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.SuggestedStartLocation = PickerLocationId.Desktop;
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        try
        {
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                VM.LuaPath = folder.Path;
                VM.SyncLuaPathToKernel();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("BrowseLua error: " + ex.Message);
        }
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        LogService.Clear();
    }

    private void CopyLogs_Click(object sender, RoutedEventArgs e)
    {
        // 复制**全量**：面板只显示尾部（PanelLines），"复制全部"必须拿到完整日志
        var text = LogService.GetText();
        if (string.IsNullOrEmpty(text)) return;

        var dataPackage = new DataPackage();
        dataPackage.SetText(text);
        Clipboard.SetContent(dataPackage);

        LogService.Event($"已复制日志到剪贴板（{LogService.Logs.Count} 行）");
    }

    private void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = LogService.LogFilePath;
            if (string.IsNullOrEmpty(path)) return;
            if (!File.Exists(path))
                File.WriteAllText(path, "");

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void OpenTokenPage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ManifestSource source)
            return;

        var url = ManifestSource.GetTokenPageUrl(source.Id);
        if (string.IsNullOrEmpty(url))
            return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("OpenTokenPage error: " + ex.Message);
        }
    }

    private void RefreshSudamaCache_Click(object sender, RoutedEventArgs e)
    {
        VM.RefreshSudamaCacheCommand.Execute(null);
    }

    private async void ImportSudamaCache_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder
            };
            picker.FileTypeFilter.Add(".json");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var files = await picker.PickMultipleFilesAsync();
            if (files.Count == 0) return;

            var paths = new List<string>();
            foreach (var f in files) paths.Add(f.Path);

            await VM.ImportSudamaCacheAsync(paths);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("ImportSudamaCache error: " + ex.Message);
        }
    }
}
