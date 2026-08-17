using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OSTGUI.ViewModels;
using System.Diagnostics;

namespace OSTGUI.Pages;

public sealed partial class NoSteamPage : Page
{
    public NoSteamViewModel VM { get; }

    public NoSteamPage(NoSteamViewModel vm)
    {
        this.InitializeComponent();
        VM = vm;
        DataContext = VM;
        Loaded += (_, _) => VM.RefreshStatus();
    }
}