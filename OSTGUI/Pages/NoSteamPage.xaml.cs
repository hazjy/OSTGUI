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

    private void RefreshStatus_Click(object sender, RoutedEventArgs e)
    {
        VM.RefreshStatus();
    }

    private void OpenGBE_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://gitlab.com/Mr_Goldberg/goldberg_emulator",
            UseShellExecute = true
        });
    }

    private void OpenSteamless_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/SteamAutoCracks/Steamless",
            UseShellExecute = true
        });
    }

    private void OpenGBEFork_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/alex47exe/gse_fork",
            UseShellExecute = true
        });
    }
}