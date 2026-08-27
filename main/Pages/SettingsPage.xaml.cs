using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OSTGUI.Models;
using OSTGUI.Services;
using OSTGUI.ViewModels;
using Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using System.Diagnostics;

namespace OSTGUI.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel VM { get; }

    private DispatcherTimer? _cacheAgeTimer;

    public SettingsPage(SettingsViewModel vm)
    {
        this.InitializeComponent();
        VM = vm;
        this.DataContext = VM;

        VM.ThemeChanged += OnThemeChanged;

        // 监听日志变更，自动更新 LogsText 并滚动到底部。
        // 必须全面防御：LogService.AddLog 可能来自任意线程（经 DispatcherQueue 封送），
        // 本处理器抛出的异常会反向炸进日志调用方，掩盖真实错误
        LogService.Logs.CollectionChanged += OnLogsChanged;

        // 每次进入页面（缓存页经 ContentFrame.Content 切换，Loaded 会重新触发，
        // OnNavigatedTo 不会）同步一次当前日志：仅在无新日志事件时，日志栏不依赖事件也有内容
        Loaded += (s, e) =>
        {
            VM.LogsText = string.Join("\n", LogService.Logs);
            VM.RefreshSudamaCacheAge();
            _cacheAgeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _cacheAgeTimer.Tick -= OnCacheAgeTick;
            _cacheAgeTimer.Tick += OnCacheAgeTick;
            _cacheAgeTimer.Start();
        };
        Unloaded += (s, e) =>
        {
            if (_cacheAgeTimer != null)
            {
                _cacheAgeTimer.Stop();
                _cacheAgeTimer.Tick -= OnCacheAgeTick;
            }
        };
    }

    private void OnCacheAgeTick(object? sender, object e)
        => VM.RefreshSudamaCacheAge();

    private void OnLogsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        try
        {
            VM.LogsText = string.Join("\n", LogService.Logs);
        }
        catch { }

        try
        {
            var dq = DispatcherQueue;
            if (dq == null) return;
            _ = dq.TryEnqueue(() =>
            {
                try
                {
                    if (LogsTextBox != null && LogsTextBox.Text != null)
                    {
                        LogsTextBox.Select(LogsTextBox.Text.Length, 0);
                    }
                }
                catch { }
            });
        }
        catch { }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (App.MainWindow is MainWindow mw)
        {
            mw.ApplyThemeAndChrome();
        }
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        // 所有设置实时保存
        VM.SaveAllToConfig();
    }

    private void DetectSteam_Click(object sender, RoutedEventArgs e)
    {
        VM.DetectSteamPathCommand.Execute(null);
    }

    private async void BrowseSteam_Click(object sender, RoutedEventArgs e)
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
                VM.SteamPath = folder.Path;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("BrowseSteam error: " + ex.Message);
        }
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        LogService.Clear();
    }

    private void CopyLogs_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(LogsTextBox.Text)) return;

        var dataPackage = new DataPackage();
        dataPackage.SetText(LogsTextBox.Text);
        Clipboard.SetContent(dataPackage);

        LogService.AddLog($"已复制日志到剪贴板");
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
