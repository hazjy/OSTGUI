using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    private readonly OstFileService _ostFileService;
    private readonly SteamGameInfoService _gameInfoService;
    private readonly SteamService _steamService;
    private readonly SteamTicketExtractor _ticketExtractor;

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

    // OST 授权文件导入/导出
    [ObservableProperty] private string _exportAppId = "";
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private string _selectedOstPath = "";
    [ObservableProperty] private OstFile? _selectedOst;
    [ObservableProperty] private bool _hasValidOst;
    [ObservableProperty] private string _ostStatusText = "";
    [ObservableProperty] private string _ostStatusType = "ok";
    [ObservableProperty] private string _ostCreatedText = "";
    [ObservableProperty] private string _ostExpiresText = "";

    public DenuvoViewModel(
        TicketService ticketService,
        LuaConfigService luaService,
        GameSearchService searchService,
        OstFileService ostFileService,
        SteamGameInfoService gameInfoService,
        SteamService steamService,
        SteamTicketExtractor ticketExtractor)
    {
        _ticketService = ticketService;
        _luaService = luaService;
        _searchService = searchService;
        _ostFileService = ostFileService;
        _gameInfoService = gameInfoService;
        _steamService = steamService;
        _ticketExtractor = ticketExtractor;
    }

    /// <summary>
    /// 导出 .ost 授权文件：优先在线提取，失败回退本机注册表缓存
    /// </summary>
    public async Task<(bool success, string message)> ExportAsync(string outputPath)
    {
        var appId = ExportAppId.Trim();
        if (string.IsNullOrEmpty(appId))
        {
            ToastService.ShowWarning("导出授权", "请先输入 AppID");
            return (false, "AppID 为空");
        }

        IsExporting = true;
        try
        {
            ToastService.ShowInfo("导出授权", $"正在从 Steam 提取 AppID {appId} 的授权，请稍候...");
            var extract = await SteamTicketExtractor.ExtractInSubprocessAsync(appId);

            if (!extract.Success)
            {
                ToastService.ShowError("导出授权", $"AppID {appId} 提取失败：{extract.Message}");
                return (false, extract.Message);
            }

            var account = _steamService.GetCurrentSteamAccount();
            var sourceName = !string.IsNullOrEmpty(account?.PersonaName)
                ? account.Value.PersonaName
                : !string.IsNullOrEmpty(account?.AccountName)
                    ? account.Value.AccountName
                    : "本地账号";

            var (ok, msg, filePath) = await _ostFileService.ExportAsync(
                appId, sourceName, extract.AppTicketHex, extract.ETicketHex, outputPath);

            if (ok)
                ToastService.ShowSuccess("导出成功", $"{filePath}\n授权有效期至 {DateTime.Now.Add(OstFileService.DefaultValidity):HH:mm}，请尽快使用");
            else
                ToastService.ShowError("导出失败", msg);
            return (ok, msg);
        }
        finally
        {
            IsExporting = false;
        }
    }

    /// <summary>
    /// 导入 .ost 授权文件：校验 → 过期警告 → 入库检查（可选补全）→ 写注册表
    /// </summary>
    public async Task<(bool success, string message)> ImportOstAsync(string filePath, XamlRoot xamlRoot)
    {
        // 1. 解析与校验
        var (ok, msg, ost) = await _ostFileService.ParseAsync(filePath);
        if (!ok || ost == null)
        {
            ToastService.ShowError("导入授权失败", msg);
            return (false, msg);
        }

        // 2. 过期警告（不强制拦截）
        if (ost.IsExpired)
        {
            var expiredDialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "授权已过期",
                Content = $"该授权已于 {ost.ExpiresAt:yyyy-MM-dd HH:mm} 过期，导入后可能无法通过 D 加密验证。\n是否仍要导入？",
                PrimaryButtonText = "仍要导入",
                CloseButtonText = "取消"
            };
            var expiredResult = await expiredDialog.ShowAsync();
            if (expiredResult != ContentDialogResult.Primary)
                return (false, "已取消导入");
        }

        // 3. 检查游戏是否已入库
        var isInLibrary = await IsInLibraryAsync(ost.AppId);
        var shouldComplete = false;
        if (!isInLibrary)
        {
            var libDialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "游戏尚未入库",
                Content = $"AppID {ost.AppId} 尚未入库。OST 内核仅对已入库（addappid）的游戏读取授权，建议先补全入库配置。\n\n补全将添加主游戏与全部 DLC 的入库条目（仅 addappid，不含清单与密钥）。",
                PrimaryButtonText = "补全入库",
                SecondaryButtonText = "仅导入授权",
                CloseButtonText = "取消"
            };
            var libResult = await libDialog.ShowAsync();
            if (libResult == ContentDialogResult.None)
                return (false, "已取消导入");
            shouldComplete = libResult == ContentDialogResult.Primary;
        }

        // 4. 可选：补全入库（主游戏 + 全部 DLC）
        if (shouldComplete)
        {
            var (luaOk, luaMsg) = await CompleteLibraryAsync(ost.AppId);
            if (!luaOk)
            {
                ToastService.ShowError("补全入库失败", luaMsg);
                return (false, luaMsg);
            }
        }

        // 5. 写入注册表凭证
        var ticket = new TicketEntry
        {
            AppId = ost.AppId,
            AppTicket = ost.AppTicket,
            ETicket = ost.ETicket,
            AccountName = ost.Source,
        };
        var (regOk, regMsg) = _ticketService.WriteTicketToRegistry(ticket);
        if (!regOk)
        {
            ToastService.ShowError("写入授权失败", regMsg);
            return (false, regMsg);
        }

        // 5.5 消费一次使用次数并写回文件（记录用，不做强制限制）
        ost.UseCount++;
        await _ostFileService.UpdateUseCountAsync(filePath, ost);

        // 6. 成功提示（系统通知）
        var hint = isInLibrary ? "" : (shouldComplete ? "，已补全入库配置" : "（游戏未入库，授权可能不生效）");
        ToastService.ShowSuccess("授权导入成功", $"AppID {ost.AppId} 已写入授权{hint}（该授权已使用 {ost.UseCount} 次）");
        return (true, "导入成功");
    }

    /// <summary>
    /// 选择 .ost 文件后解析并展示元数据（仅合法文件才置为可用）
    /// </summary>
    public async Task LoadOstPreviewAsync(string filePath)
    {
        var (ok, msg, ost) = await _ostFileService.ParseAsync(filePath);
        if (!ok || ost == null)
        {
            ClearOstSelection();
            ToastService.ShowError("授权文件无效", msg);
            return;
        }

        SelectedOst = ost;
        SelectedOstPath = filePath;
        HasValidOst = true;
        OstStatusText = ost.IsExpired ? "已过期" : "有效";
        OstStatusType = ost.IsExpired ? "error" : "ok";
        OstCreatedText = ost.CreatedAt.ToString("yyyy-MM-dd HH:mm");
        OstExpiresText = ost.ExpiresAt.ToString("yyyy-MM-dd HH:mm");
    }

    /// <summary>
    /// 清空当前 .ost 选择
    /// </summary>
    public void ClearOstSelection()
    {
        SelectedOst = null;
        SelectedOstPath = "";
        HasValidOst = false;
        OstStatusText = "";
        OstStatusType = "ok";
        OstCreatedText = "";
        OstExpiresText = "";
    }

    /// <summary>
    /// 判断游戏是否已入库（Lua 中存在 addappid）
    /// </summary>
    private async Task<bool> IsInLibraryAsync(string appId)
    {
        try
        {
            var lua = await _luaService.ReadLuaContentAsync(appId);
            return lua != null && Regex.IsMatch(
                lua,
                $@"addappid\s*\(\s*{Regex.Escape(appId)}\b",
                RegexOptions.IgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 补全入库配置：addappid 主游戏 + 全部 DLC（仅入库条目，不含清单/密钥）
    /// </summary>
    private async Task<(bool success, string message)> CompleteLibraryAsync(string appId)
    {
        try
        {
            var lines = new List<string>
            {
                $"-- OSTGUI 授权导入补全 - AppID {appId}",
                $"addappid({appId})",
            };

            var dlcIds = await _gameInfoService.GetDlcIdsAsync(appId);
            if (dlcIds.Count > 0)
            {
                lines.Add("-- 所有 DLC");
                lines.AddRange(dlcIds.Select(dlcId => $"addappid({dlcId})"));
            }

            var (ok, msg, _) = await _luaService.WriteLuaFileAsync(
                appId, string.Join("\n", lines) + "\n");
            return (ok, msg);
        }
        catch (Exception ex)
        {
            return (false, $"补全入库失败: {ex.Message}");
        }
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
