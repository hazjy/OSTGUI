using CommunityToolkit.Mvvm.ComponentModel;
using OSTGUI.Services;

namespace OSTGUI.ViewModels;

public partial class NoSteamViewModel : ObservableObject
{
    private readonly SteamService _steamService;
    private readonly SteamDllService _steamDllService;

    [ObservableProperty] private string _statusMessage = "就绪";
    [ObservableProperty] private string _statusType = "Info";

    public NoSteamViewModel(SteamService steamService, SteamDllService steamDllService)
    {
        _steamService = steamService;
        _steamDllService = steamDllService;
    }

    public void RefreshStatus()
    {
        var isSteamRunning = _steamService.IsSteamRunning();
        var isInjected = _steamDllService.IsOSTDllInjected();

        StatusMessage = isSteamRunning ? "Steam 正在运行" : "Steam 未运行";
        StatusType = isSteamRunning ? "Warning" : "Success";
    }
}