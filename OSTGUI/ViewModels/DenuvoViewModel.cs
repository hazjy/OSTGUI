using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OSTGUI.Models;
using OSTGUI.Services;

namespace OSTGUI.ViewModels;

/// <summary>
/// D加密切号授权管理 ViewModel
/// </summary>
public partial class DenuvoViewModel : ObservableObject
{
    private readonly TicketService _ticketService;
    private readonly LuaConfigService _luaService;
    private readonly GameSearchService _searchService;

    [ObservableProperty] private ObservableCollection<TicketProfile> _profiles = new();
    [ObservableProperty] private TicketProfile? _selectedProfile;
    [ObservableProperty] private ObservableCollection<TicketEntry> _currentTickets = new();
    [ObservableProperty] private TicketEntry? _selectedTicket;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "管理 D加密游戏授权方案";
    [ObservableProperty] private string _statusType = "Info";

    // 添加/编辑表单
    [ObservableProperty] private string _editAppId = "";
    [ObservableProperty] private string _editAppTicket = "";
    [ObservableProperty] private string _editETicket = "";
    [ObservableProperty] private string _editAccountName = "";
    [ObservableProperty] private string _editNotes = "";
    [ObservableProperty] private bool _isEditing;

    // 导入
    [ObservableProperty] private string _importText = "";
    [ObservableProperty] private string _importAccountName = "";

    public DenuvoViewModel(
        TicketService ticketService,
        LuaConfigService luaService,
        GameSearchService searchService)
    {
        _ticketService = ticketService;
        _luaService = luaService;
        _searchService = searchService;
    }

    [RelayCommand]
    private async Task LoadProfilesAsync()
    {
        IsLoading = true;
        try
        {
            var profiles = await _ticketService.LoadProfilesAsync();
            Profiles = new ObservableCollection<TicketProfile>(profiles);
            SelectedProfile = profiles.FirstOrDefault(p => p.IsActive);
            if (SelectedProfile != null)
            {
                CurrentTickets = new ObservableCollection<TicketEntry>(SelectedProfile.Tickets);
                await RefreshTicketNamesAsync();
            }
            SetStatus($"已加载 {profiles.Count} 个方案", "Info");
        }
        catch (Exception ex)
        {
            SetStatus($"加载失败: {ex.Message}", "Error");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 刷新游戏名称
    /// </summary>
    private async Task RefreshTicketNamesAsync()
    {
        var appIds = CurrentTickets
            .Where(t => string.IsNullOrEmpty(t.GameName) || t.GameName.StartsWith("AppID"))
            .Select(t => t.AppId)
            .Distinct()
            .ToList();

        if (appIds.Count == 0) return;

        var names = await _searchService.GetGameNamesBatchAsync(appIds);
        foreach (var ticket in CurrentTickets)
        {
            if (names.TryGetValue(ticket.AppId, out var name))
                ticket.GameName = name;
        }
    }

    [RelayCommand]
    private void SelectProfile(TicketProfile? profile)
    {
        if (profile == null) return;
        SelectedProfile = profile;
        CurrentTickets = new ObservableCollection<TicketEntry>(profile.Tickets);
    }

    /// <summary>
    /// 开始添加新 ticket
    /// </summary>
    [RelayCommand]
    private void StartAddTicket()
    {
        IsEditing = true;
        EditAppId = "";
        EditAppTicket = "";
        EditETicket = "";
        EditAccountName = "";
        EditNotes = "";
    }

    /// <summary>
    /// 保存 ticket
    /// </summary>
    [RelayCommand]
    private async Task SaveTicketAsync()
    {
        if (string.IsNullOrWhiteSpace(EditAppId))
        {
            SetStatus("请输入 AppID", "Warning");
            return;
        }

        if (string.IsNullOrWhiteSpace(EditAppTicket) && string.IsNullOrWhiteSpace(EditETicket))
        {
            SetStatus("请至少填入 AppTicket 或 ETicket", "Warning");
            return;
        }

        var ticket = new TicketEntry
        {
            AppId = EditAppId.Trim(),
            AppTicket = EditAppTicket.Trim(),
            ETicket = EditETicket.Trim(),
            AccountName = EditAccountName.Trim(),
            Notes = EditNotes.Trim(),
        };

        // 获取游戏名
        var result = await _searchService.SearchByAppIdAsync(ticket.AppId);
        if (result.Success) ticket.GameName = result.Name;

        if (SelectedProfile != null)
        {
            SelectedProfile.Tickets.Add(ticket);
            CurrentTickets = new ObservableCollection<TicketEntry>(SelectedProfile.Tickets);
        }

        IsEditing = false;
        SetStatus($"已添加 AppID {ticket.AppId} ({ticket.GameName})", "Success");
    }

    /// <summary>
    /// 删除 ticket
    /// </summary>
    [RelayCommand]
    private void DeleteTicket(TicketEntry? ticket)
    {
        if (ticket == null || SelectedProfile == null) return;
        SelectedProfile.Tickets.Remove(ticket);
        CurrentTickets = new ObservableCollection<TicketEntry>(SelectedProfile.Tickets);
        SetStatus($"已移除 AppID {ticket.AppId}", "Info");
    }

    /// <summary>
    /// 写入 ticket 到注册表
    /// </summary>
    [RelayCommand]
    private void WriteTicketToRegistry(TicketEntry? ticket)
    {
        if (ticket == null) return;

        var (success, message) = _ticketService.WriteTicketToRegistry(ticket);
        SetStatus(message, success ? "Success" : "Error");
    }

    /// <summary>
    /// 从注册表删除 ticket
    /// </summary>
    [RelayCommand]
    private void DeleteTicketFromRegistry(TicketEntry? ticket)
    {
        if (ticket == null) return;

        var (success, message) = _ticketService.DeleteTicketFromRegistry(ticket.AppId);
        SetStatus(message, success ? "Success" : "Error");
    }

    /// <summary>
    /// 应用当前方案到系统
    /// </summary>
    [RelayCommand]
    private async Task ApplyProfileAsync()
    {
        if (SelectedProfile == null)
        {
            SetStatus("请先选择一个方案", "Warning");
            return;
        }

        SetStatus("正在应用方案...", "Info");
        var (success, message) = await _ticketService.ApplyProfileAsync(SelectedProfile);
        SetStatus(message, success ? "Success" : "Error");
    }

    /// <summary>
    /// 读取注册表中的 ticket
    /// </summary>
    [RelayCommand]
    private void ReadTicketFromRegistry(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return;

        var ticket = _ticketService.ReadTicketFromRegistry(appId);
        if (ticket != null)
        {
            SetStatus($"已读取 AppID {appId} 的授权数据", "Success");
            EditAppId = ticket.AppId;
            EditAppTicket = ticket.AppTicket;
            EditETicket = ticket.ETicket;
            IsEditing = true;
        }
        else
        {
            SetStatus($"AppID {appId} 在注册表中无授权数据", "Warning");
        }
    }

    /// <summary>
    /// 从文本导入 tickets（extract_tickets 输出）
    /// </summary>
    [RelayCommand]
    private void ImportTicketsFromText()
    {
        if (string.IsNullOrWhiteSpace(ImportText))
        {
            SetStatus("请粘贴 tickets.txt 的内容", "Warning");
            return;
        }

        var tickets = TicketService.ParseTicketsFromText(ImportText, ImportAccountName);
        if (tickets.Count == 0)
        {
            SetStatus("未解析到有效的 ticket 数据", "Error");
            return;
        }

        if (SelectedProfile != null)
        {
            SelectedProfile.Tickets.AddRange(tickets);
            CurrentTickets = new ObservableCollection<TicketEntry>(SelectedProfile.Tickets);
        }

        ImportText = "";
        SetStatus($"成功导入 {tickets.Count} 个 ticket", "Success");
    }

    /// <summary>
    /// 添加新方案
    /// </summary>
    [RelayCommand]
    private void AddProfile()
    {
        var profile = new TicketProfile
        {
            Name = $"方案 {Profiles.Count + 1}",
            IsActive = false
        };
        Profiles.Add(profile);
        SelectedProfile = profile;
        CurrentTickets = new ObservableCollection<TicketEntry>();
    }

    /// <summary>
    /// 保存所有方案
    /// </summary>
    [RelayCommand]
    private async Task SaveProfilesAsync()
    {
        await _ticketService.SaveProfilesAsync(Profiles.ToList());
        SetStatus("方案已保存", "Success");
    }

    private void SetStatus(string message, string type)
    {
        StatusMessage = message;
        StatusType = type;
    }
}