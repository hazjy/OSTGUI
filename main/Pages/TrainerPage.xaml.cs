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
        if (VM == null || SourceSegmented.SelectedIndex < 0) return;

        VM.ViewIndex = SourceSegmented.SelectedIndex;

        // 两个视图的输入框与列表都是各自独立的（搜索=Items，已下载=LocalItems），
        // 只切可见性：共用集合时，搜索请求晚回来会把结果糊到"已下载"上
        var local = SourceSegmented.SelectedIndex == 1;
        WebSearchRow.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        LocalFilterRow.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        ResultsList.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        LocalList.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LocalFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (VM != null && sender is TextBox box) VM.LocalFilter = box.Text ?? "";
    }

    private void QueryBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) VM.SearchCommand.Execute(null);
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TrainerInfo info) VM.DownloadCommand.Execute(info);
    }

    /// <summary>「更多」菜单针对的条目（MenuFlyout 是页面级的，菜单项拿不到行数据）</summary>
    private TrainerInfo? _menuItem;

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button || button.Tag is not TrainerInfo item) return;

        _menuItem = item;
        ((MenuFlyout)Resources["MoreMenu"]).ShowAt(button, new Windows.Foundation.Point(0, button.ActualHeight));
    }

    /// <summary>复制 exe 的完整文件名（= 索引里的名称，也是绑定用的名字）</summary>
    private void CopyName_Click(object sender, RoutedEventArgs e)
    {
        if (_menuItem == null) return;

        var name = Path.GetFileName(_menuItem.LocalPath);
        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText(name);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        (App.MainWindow as MainWindow)?.Notify("已复制");   // 应用内通知（窗口顶部）
    }

    /// <summary>菜单项没有 Tag，条目来自页面级的 _menuItem（由 More_Click 设置）</summary>
    private void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_menuItem == null)
        {
            LogService.AddAppLog("trainer 菜单「更新」但 _menuItem 为空（菜单未从行上打开？）");
            return;
        }
        VM.UpdateCommand.Execute(_menuItem);
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
        if (_menuItem == null)
        {
            LogService.AddAppLog("trainer 菜单「删除」但 _menuItem 为空（菜单未从行上打开？）");
            return;
        }
        VM.DeleteCommand.Execute(_menuItem);
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
    /// <summary>选下载目录：取消就什么都不改；选定后只改内存（退出时统一落盘），立刻生效</summary>
    private async void ChooseDir_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
        };
        picker.FileTypeFilter.Add("*");   // FolderPicker 至少要一个过滤项，否则 WinUI 会抛异常

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        VM.SetDownloadDir(folder.Path);
    }

    private async void AddBinding_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot == null) return;

        // 修改器：输入名称 + 「查询」（名称从「已下载」里用「复制名称」拿，比在下拉里翻找省事）
        var trainerBox = new TextBox
        {
            PlaceholderText = "请在更多（三个点）里复制",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 320,
        };
        var lookupButton = new Button { Content = "查询" };
        var lookupText = new TextBlock
        {
            Text = "",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            // 刻意不设 Foreground：Application.Current.Resources 取到的是系统主题的画刷，
            // 浅色应用 + 深色系统会把字染错（项目里踩过），交给弹窗主题继承
        };
        var trainerRow = new Grid { ColumnSpacing = 8 };
        trainerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        trainerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(trainerBox, 0);
        Grid.SetColumn(lookupButton, 1);
        trainerRow.Children.Add(trainerBox);
        trainerRow.Children.Add(lookupButton);

        var resolvedPath = "";

        void Lookup()
        {
            resolvedPath = VM.FindTrainerPath(trainerBox.Text ?? "") ?? "";
            lookupText.Text = resolvedPath.Length > 0
                ? $"找到：{resolvedPath}"
                : "没找到这个名称的修改器（名称要完全一致，可在「已下载」里复制）";
        }

        lookupButton.Click += (_, _) => Lookup();
        var exeText = new TextBlock { Text = "", FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var pickButton = new Button { Content = "选择" };
        var gameExe = "";

        pickButton.Click += async (_, _) =>
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".exe");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            gameExe = file.Path;
            exeText.Text = gameExe;
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "修改器名称" });
        panel.Children.Add(trainerRow);
        panel.Children.Add(lookupText);
        panel.Children.Add(new TextBlock { Text = "游戏主程序" });
        panel.Children.Add(pickButton);
        panel.Children.Add(exeText);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "添加绑定",
            Content = panel,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
        };

        // 校验就地做：不满足就让弹窗不关（没查询/查不到时顺手查一次，结果显示在下方小字里）
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (resolvedPath.Length == 0) Lookup();
            if (resolvedPath.Length == 0 || gameExe.Length == 0 || !File.Exists(gameExe)) args.Cancel = true;
        };

        Helpers.PopupTheme.Apply(dialog);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        VM.AddOrUpdateBinding(trainerBox.Text!.Trim(), gameExe, enabled: true);

        // 绑了却不启监控＝不生效，容易踩；顺手替用户打开（关掉随时可以）
        if (!VM.MonitorEnabled) VM.MonitorEnabled = true;
    }
}
