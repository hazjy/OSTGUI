using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OSTGUI.Models;
using OSTGUI.Services;
using OSTGUI.ViewModels;
using Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace OSTGUI.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel VM { get; }
    public ObservableCollection<string> Logs => LogService.Logs;

    public SettingsPage(SettingsViewModel vm)
    {
        this.InitializeComponent();
        VM = vm;
        this.DataContext = VM;

        VM.ThemeChanged += OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (App.MainWindow is MainWindow mw)
        {
            VM.ApplyTheme(mw.Root);
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
        if (LogService.Logs.Count == 0) return;

        var text = string.Join("\n", LogService.Logs);
        var dataPackage = new DataPackage();
        dataPackage.SetText(text);
        Clipboard.SetContent(dataPackage);

        LogService.AddLog($"已复制 {LogService.Logs.Count} 条日志到剪贴板");
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
}
