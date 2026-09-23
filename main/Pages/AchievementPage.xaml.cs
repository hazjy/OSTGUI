using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using OSTGUI.ViewModels;

namespace OSTGUI.Pages;

public sealed partial class AchievementPage : Page
{
    public AchievementViewModel VM { get; }

    /// <summary>无参构造：`Frame.Navigate` 需要它（VM 不挂 MainViewModel，直接走 DI）</summary>
    public AchievementPage() : this(App.Services.GetRequiredService<AchievementViewModel>()) { }

    public AchievementPage(AchievementViewModel vm)
    {
        this.InitializeComponent();
        VM = vm;
        DataContext = VM;
        Loaded += async (_, _) => await VM.InitializeAsync();
    }
}
