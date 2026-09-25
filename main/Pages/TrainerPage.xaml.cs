using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OSTGUI.Models;
using OSTGUI.Services;
using OSTGUI.ViewModels;

namespace OSTGUI.Pages;

public sealed partial class TrainerPage : Page
{
    public TrainerViewModel VM { get; }

    /// <summary>无参构造：`Frame.Navigate` 需要它（VM 走 DI，和成就页同款）</summary>
    public TrainerPage() : this(App.Services.GetRequiredService<TrainerViewModel>()) { }

    public TrainerPage(TrainerViewModel vm)
    {
        this.InitializeComponent();
        VM = vm;
        DataContext = VM;
        Loaded += async (_, _) => await VM.InitializeAsync();
    }

    private void SourceSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 注意：Segmented 第一项的 IsSelected="True" 会在 InitializeComponent() **期间**就触发本事件，
        // 那时 VM 还没赋值（构造里 InitializeComponent 在前）——不判空会在导航时抛 NRE、页面打不开
        if (VM != null && SourceSegmented.SelectedIndex >= 0) VM.ViewIndex = SourceSegmented.SelectedIndex;
    }

    private void QueryBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) VM.SearchCommand.Execute(null);
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TrainerInfo info) VM.DownloadCommand.Execute(info);
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TrainerInfo info) VM.LaunchCommand.Execute(info);
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TrainerInfo info) VM.RevealCommand.Execute(info);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TrainerInfo info) VM.DeleteCommand.Execute(info);
    }

    private void RemoveBinding_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TrainerBinding binding) VM.RemoveBindingCommand.Execute(binding);
    }

    private void BindingEnabled_Click(object sender, RoutedEventArgs e)
    {
        // 勾选靠手动回写：SetBindingEnabled 会存盘并重算"监控该不该跑"
        if (sender is CheckBox box && box.Tag is TrainerBinding binding)
            VM.SetBindingEnabled(binding, box.IsChecked == true);
    }

    /// <summary>
    /// 添加绑定：选游戏（自动带出主程序，找不到可手选）→ 选已下载的修改器 → 是否启用。
    /// 对话框用代码搭（与入库管理页同风格），读到的结果交给 VM 统一落盘。
    /// </summary>
    private async void AddBinding_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot == null) return;

        if (VM.LocalTrainers.Count == 0)
        {
            ToastService.ShowWarning("先下载一个修改器", "「已下载」里还没有可绑定的修改器");
            return;
        }

        var gameBox = new ComboBox
        {
            ItemsSource = VM.Games,
            DisplayMemberPath = "GameName",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 320,
        };
        var trainerBox = new ComboBox
        {
            ItemsSource = VM.LocalTrainers,
            DisplayMemberPath = "GameName",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 320,
            SelectedIndex = 0,
        };
        var exeText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var pickButton = new Button { Content = "手选游戏主程序…" };
        var enabledBox = new CheckBox { Content = "启用（游戏运行时自动启动修改器）", IsChecked = true };

        var gameExe = "";

        void SyncGameExe()
        {
            var game = gameBox.SelectedItem as LibraryItem;
            gameExe = game == null ? "" : VM.ResolveGameExe(game.AppId) ?? "";
            exeText.Text = game == null
                ? "没选游戏（也可以直接手选主程序）"
                : gameExe.Length > 0
                    ? $"主程序：{gameExe}"
                    : "客户端里没找到这个游戏的主程序（可能没安装）——请手选";
        }

        gameBox.SelectionChanged += (_, _) => SyncGameExe();
        pickButton.Click += async (_, _) =>
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".exe");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            gameExe = file.Path;
            exeText.Text = $"主程序：{gameExe}";
        };

        SyncGameExe();

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "游戏" });
        panel.Children.Add(gameBox);
        panel.Children.Add(pickButton);
        panel.Children.Add(exeText);
        panel.Children.Add(new TextBlock { Text = "修改器" });
        panel.Children.Add(trainerBox);
        panel.Children.Add(enabledBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "添加绑定",
            Content = panel,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
        };

        Helpers.PopupTheme.Apply(dialog);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (trainerBox.SelectedItem is not TrainerInfo trainer || !File.Exists(trainer.LocalPath))
        {
            ToastService.ShowError("绑定失败", "没有选到可用的修改器文件");
            return;
        }

        if (gameExe.Length == 0 || !File.Exists(gameExe))
        {
            ToastService.ShowError("绑定失败", "没有可用的游戏主程序——先选游戏或手选一个 exe");
            return;
        }

        var picked = gameBox.SelectedItem as LibraryItem;
        VM.AddOrUpdateBinding(
            picked?.AppId ?? "",
            picked?.GameName ?? Path.GetFileNameWithoutExtension(gameExe),
            gameExe,
            trainer.LocalPath,
            enabledBox.IsChecked == true);

        // 绑了却不启监控＝不生效，容易踩；顺手替用户打开（关掉随时可以）
        if (enabledBox.IsChecked == true && !VM.MonitorEnabled) VM.MonitorEnabled = true;
    }
}
