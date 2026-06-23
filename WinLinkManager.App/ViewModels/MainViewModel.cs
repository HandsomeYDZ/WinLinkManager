using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WinLinkManager.App.ViewModels.Base;
using WinLinkManager.App.Views;
using WinLinkManager.Core.Models;
using WinLinkManager.Core.Services;

namespace WinLinkManager.App.ViewModels;

/// <summary>
/// 主界面 ViewModel，负责链接列表展示、扫描、搜索、过滤、白名单管理和链接操作。
/// </summary>
public class MainViewModel : ViewModelBase
{
    private readonly IIndexService _indexService;
    private readonly ILinkService _linkService;
    private readonly IWhitelistService _whitelistService;
    private readonly IScannerService _scannerService;
    private readonly IUsnMonitorService _usnMonitorService;
    private readonly ILogger<MainViewModel> _logger;
    private readonly SynchronizationContext _uiContext;
    private CancellationTokenSource? _searchDebounceCts;
    private CancellationTokenSource? _refreshDebounceCts;
    private Timer? _healthCheckTimer;
    private string _searchTextLower = string.Empty;

    /// <summary> 当前加载的所有链接条目集合。 </summary>
    public ObservableCollection<LinkEntry> Links { get; } = new();

    private ICollectionView? _linksView;
    /// <summary> 链接列表的视图，支持过滤和排序。 </summary>
    public ICollectionView LinksView =>
        _linksView ??= CollectionViewSource.GetDefaultView(Links);

    // ── 标签切换 ──
    private bool _showAllLinks = true;
    /// <summary> 是否显示所有链接。 </summary>
    public bool ShowAllLinks
    {
        get => _showAllLinks;
        set
        {
            if (SetProperty(ref _showAllLinks, value) && value)
            {
                _showWhitelistOnly = false;
                OnPropertyChanged(nameof(ShowWhitelistOnly));
                LinksView.Refresh();
            }
        }
    }

    private bool _showWhitelistOnly;
    /// <summary> 是否仅显示白名单中的链接。 </summary>
    public bool ShowWhitelistOnly
    {
        get => _showWhitelistOnly;
        set
        {
            if (SetProperty(ref _showWhitelistOnly, value) && value)
            {
                _showAllLinks = false;
                OnPropertyChanged(nameof(ShowAllLinks));
                LinksView.Refresh();
            }
        }
    }

    // ── 搜索 ──
    private string _searchText = string.Empty;
    /// <summary> 搜索关键词，变更时自动触发防抖搜索。 </summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _searchTextLower = (value ?? string.Empty).ToLowerInvariant();
                DebounceSearch();
            }
        }
    }

    // ── 扫描状态 ──
    private bool _isScanning;
    /// <summary> 是否正在扫描中，影响命令可用性和按钮文字。 </summary>
    public bool IsScanning
    {
        get => _isScanning;
        set
        {
            if (SetProperty(ref _isScanning, value))
            {
                ((RelayCommand)ScanOrRebuildCommand).RaiseCanExecuteChanged();
                ((RelayCommand)RefreshCommand).RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(ScanButtonLabel));
            }
        }
    }

    private ScanProgress? _scanProgress;
    /// <summary> 当前扫描进度。 </summary>
    public ScanProgress? ScanProgress
    {
        get => _scanProgress;
        set
        {
            if (SetProperty(ref _scanProgress, value))
                OnPropertyChanged(nameof(ScanProgressText));
        }
    }

    private string _statusMessage = "就绪";
    /// <summary> 底部状态栏信息。 </summary>
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary> 格式化后的扫描进度文本。 </summary>
    public string ScanProgressText =>
        ScanProgress is null ? "" :
        ScanProgress.IsComplete ? "扫描完成" :
        $"当前: {ScanProgress.CurrentDirectory ?? "..."}  (已扫 {ScanProgress.TotalScanned:N0} 项, 找到 {ScanProgress.LinksFound} 个链接)";

    /// <summary> 扫描按钮的文字标签。 </summary>
    public string ScanButtonLabel =>
        IsScanning ? "扫描中..." :
        Links.Count == 0 ? "建立索引" : "重建索引";

    private LinkEntry? _selectedLink;
    private IList<LinkEntry> _selectedLinks = new List<LinkEntry>();

    /// <summary> 当前选中的多个链接（用于批量操作）。 </summary>
    public IList<LinkEntry> SelectedLinks
    {
        get => _selectedLinks;
        set
        {
            if (SetProperty(ref _selectedLinks, value))
            {
                ((RelayCommand)AddSelectedToWhitelistCommand).RaiseCanExecuteChanged();
                ((RelayCommand)RemoveSelectedFromWhitelistCommand).RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary> 当前选中的单个链接，变更时刷新所有关联命令的状态。 </summary>
    public LinkEntry? SelectedLink
    {
        get => _selectedLink;
        set
        {
            if (SetProperty(ref _selectedLink, value))
            {
                ((RelayCommand)OpenLocationCommand).RaiseCanExecuteChanged();
                ((RelayCommand)OpenTargetLocationCommand).RaiseCanExecuteChanged();
                ((RelayCommand)DeleteLinkCommand).RaiseCanExecuteChanged();
                ((RelayCommand)EditLinkCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ConvertToJunctionCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ConvertToDirectoryLinkCommand).RaiseCanExecuteChanged();
                ((RelayCommand)CopyLinkPathCommand).RaiseCanExecuteChanged();
                ((RelayCommand)CopyTargetPathCommand).RaiseCanExecuteChanged();
                ((RelayCommand)AddToWhitelistCommand).RaiseCanExecuteChanged();
                ((RelayCommand)RemoveFromWhitelistCommand).RaiseCanExecuteChanged();
                ((RelayCommand)AddSelectedToWhitelistCommand).RaiseCanExecuteChanged();
                ((RelayCommand)RemoveSelectedFromWhitelistCommand).RaiseCanExecuteChanged();
            }
        }
    }

    // ── 命令 ──
    public ICommand OpenLocationCommand { get; }
    public ICommand OpenTargetLocationCommand { get; }
    public ICommand DeleteLinkCommand { get; }
    public ICommand ConvertToJunctionCommand { get; }
    public ICommand ConvertToDirectoryLinkCommand { get; }
    public ICommand CopyLinkPathCommand { get; }
    public ICommand CopyTargetPathCommand { get; }
    public ICommand AddToWhitelistCommand { get; }
    public ICommand RemoveFromWhitelistCommand { get; }
    public ICommand AddSelectedToWhitelistCommand { get; }
    public ICommand RemoveSelectedFromWhitelistCommand { get; }
    public ICommand CreateNewLinkCommand { get; }
    public ICommand EditLinkCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ScanOrRebuildCommand { get; }
    public ICommand OpenSettingsCommand { get; }

    /// <summary> 通过 DI 注入所有必需服务，注册 USN 变更事件并初始化所有命令。 </summary>
    public MainViewModel(
        IIndexService indexService,
        ILinkService linkService,
        IWhitelistService whitelistService,
        IScannerService scannerService,
        IUsnMonitorService usnMonitorService,
        ILogger<MainViewModel> logger)
    {
        _indexService = indexService;
        _linkService = linkService;
        _whitelistService = whitelistService;
        _scannerService = scannerService;
        _usnMonitorService = usnMonitorService;
        _logger = logger;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();

        // 订阅文件系统变更事件，触发自动刷新
        _usnMonitorService.ChangeDetected += OnFileChangeDetected;

        OpenLocationCommand = new RelayCommand(OpenLocation, () => SelectedLink is not null);
        OpenTargetLocationCommand = new RelayCommand(OpenTargetLocation, () => SelectedLink is not null);
        DeleteLinkCommand = new RelayCommand(DeleteLink, () => SelectedLink is not null);
        ConvertToJunctionCommand = new RelayCommand(ConvertToJunction, () => SelectedLink is not null && CanConvertToJunction());
        ConvertToDirectoryLinkCommand = new RelayCommand(ConvertToDirectoryLink, () => SelectedLink is not null && CanConvertToDirectoryLink());
        CopyLinkPathCommand = new RelayCommand(CopyLinkPath, () => SelectedLink is not null);
        CopyTargetPathCommand = new RelayCommand(CopyTargetPath, () => SelectedLink is not null);
        AddToWhitelistCommand = new RelayCommand(AddToWhitelist, () => SelectedLink is not null && !SelectedLink.InWhitelist);
        RemoveFromWhitelistCommand = new RelayCommand(RemoveFromWhitelist, () => SelectedLink is not null && SelectedLink.InWhitelist);
        AddSelectedToWhitelistCommand = new RelayCommand(async () => await AddSelectedToWhitelist(), CanAddSelectedToWhitelist);
        RemoveSelectedFromWhitelistCommand = new RelayCommand(async () => await RemoveSelectedFromWhitelist(), CanRemoveSelectedFromWhitelist);
        CreateNewLinkCommand = new RelayCommand(CreateNewLink);
        EditLinkCommand = new RelayCommand(EditLink, () => SelectedLink is not null);
        RefreshCommand = new RelayCommand(async () => await RefreshAsync(), () => !IsScanning);
        ScanOrRebuildCommand = new RelayCommand(async () => await ScanOrRebuildAsync(), () => !IsScanning);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
    }

    /// <summary> 首次初始化：加载已有索引或执行首次扫描，设置过滤/排序和健康检查定时器。 </summary>
    public async Task InitializeAsync()
    {
        if (!await _indexService.HasExistingIndexAsync())
        {
            await RunScanAsync("正在建立首次索引...");
        }
        else
        {
            var links = await _indexService.GetAllAsync();
            await RefreshEntryStatusesAsync(links);
            foreach (var link in links)
                Links.Add(link);
            StatusMessage = $"已加载 {Links.Count} 个符号链接";
        }

        LinksView.Filter = o => FilterLink(o as LinkEntry);
        LinksView.SortDescriptions.Add(new SortDescription(nameof(LinkEntry.LinkName), ListSortDirection.Ascending));

        // 低频率健康检查：每 5 分钟校验所有链接的目标是否存在
        _healthCheckTimer = new Timer(_ => _ = PeriodicHealthCheckAsync(), null,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary> 重建索引：清空后重新全盘扫描。 </summary>
    private async Task ScanOrRebuildAsync()
    {
        if (IsScanning) return;

        if (Links.Count > 0)
        {
            var result = MessageBox.Show("确定要重建索引吗？这将清空当前所有扫描结果。",
                "重建索引", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
        }

        Links.Clear();
        await _indexService.RebuildIndexAsync();
        await RunScanAsync("正在扫描...");
    }

    /// <summary> 执行全盘扫描，通过 Progress 实时汇报进度并逐条添加到集合。 </summary>
    private async Task RunScanAsync(string initialMessage)
    {
        IsScanning = true;
        StatusMessage = initialMessage;

        try
        {
            var scanDirs = await _indexService.GetScanDirectoriesAsync();
            // 通过 Progress<T> 将扫描进度回传到 UI 线程
            var progress = new Progress<ScanProgress>(p =>
            {
                _uiContext.Post(_ =>
                {
                    ScanProgress = p;
                    StatusMessage = $"扫描中... 已扫描 {p.TotalScanned:N0} 项, 发现 {p.LinksFound} 个链接";
                }, null);
            });

            // 加载白名单路径，重建索引后恢复白名单状态
            var whitelistPaths = await _whitelistService.GetAllPathsAsync();
            var whitelistSet = new HashSet<string>(whitelistPaths, StringComparer.OrdinalIgnoreCase);

            // MFT 枚举和重解析点分析是同步、CPU/I/O 密集型工作，必须离开 UI 线程。
            await Task.Run(async () =>
            {
                await foreach (var entry in _scannerService.FullScanAsync(
                                   scanDirs, progress, CancellationToken.None))
                {
                    // 恢复白名单状态：若该路径在白名单中，标记 InWhitelist
                    if (whitelistSet.Contains(entry.LinkPath))
                        entry.InWhitelist = true;
                    await _indexService.UpsertAsync(entry);
                    _uiContext.Post(_ => Links.Add(entry), null);
                }
            });
            StatusMessage = $"扫描完成，共发现 {Links.Count} 个链接";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "扫描失败");
            StatusMessage = $"扫描失败: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    // ── 过滤 ──
    /// <summary> 根据白名单/搜索词过滤链接条目。 </summary>
    private bool FilterLink(LinkEntry? entry)
    {
        if (entry is null) return false;

        // 白名单模式下隐藏非白名单条目
        if (_showWhitelistOnly && !entry.InWhitelist)
            return false;

        // 搜索词匹配：名称、路径、目标路径任一命中即可
        if (_searchTextLower.Length > 0)
        {
            return entry.LinkName.IndexOf(_searchTextLower, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   entry.LinkPath.IndexOf(_searchTextLower, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   entry.TargetPath.IndexOf(_searchTextLower, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        return true;
    }

    /// <summary> 搜索防抖：200ms 内无新输入才刷新视图，避免频繁过滤。 </summary>
    private void DebounceSearch()
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts = new CancellationTokenSource();
        var token = _searchDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200, token);
                _uiContext.Post(_ => LinksView.Refresh(), null);
            }
            catch (TaskCanceledException) { }
        }, token);
    }

    // ── 命令实现 ──
    /// <summary> 在资源管理器中打开链接所在目录。 </summary>
    private void OpenLocation()
    {
        var entry = SelectedLink;
        if (entry is null) return;
        var dir = Path.GetDirectoryName(entry.LinkPath);
        if (!string.IsNullOrEmpty(dir))
            System.Diagnostics.Process.Start("explorer.exe", dir);
    }

    /// <summary> 删除选中链接：确认对话框后执行删除并更新索引。 </summary>
    private async void DeleteLink()
    {
        var entry = SelectedLink;
        if (entry is null) return;

        // 弹出删除确认对话框
        var vm = new DeleteConfirmViewModel { Entry = entry };
        var dialog = new DeleteConfirmDialog { DataContext = vm };
        dialog.Owner = Application.Current.MainWindow;
        if (dialog.ShowDialog() != true) return;

        try
        {
            _linkService.DeleteLink(entry.LinkPath, entry.LinkType);
            await _indexService.DeleteAsync(entry.LinkPath);
            _uiContext.Post(_ => Links.Remove(entry), null);
            StatusMessage = $"已删除: {entry.LinkName}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除链接失败: {LinkPath}", entry.LinkPath);
            MessageBox.Show($"删除失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary> 检查当前选中链接是否能转换为交接点（文件/目录链接不可转）。 </summary>
    private bool CanConvertToJunction()
        => SelectedLink is not null && SelectedLink.LinkType != LinkType.Junction && SelectedLink.LinkType != LinkType.FileLink;

    /// <summary> 将选中链接转换为交接点。 </summary>
    private async void ConvertToJunction()
    {
        var entry = SelectedLink;
        if (entry is null || !CanConvertToJunction()) return;
        await ConvertLinkType(entry, LinkType.Junction);
    }

    /// <summary> 检查当前选中链接是否能转换为目录符号链接（目录/文件链接不可转）。 </summary>
    private bool CanConvertToDirectoryLink()
        => SelectedLink is not null && SelectedLink.LinkType is not LinkType.DirectoryLink and not LinkType.FileLink;

    /// <summary> 将选中链接转换为目录符号链接。 </summary>
    private async void ConvertToDirectoryLink()
    {
        var entry = SelectedLink;
        if (entry is null || !CanConvertToDirectoryLink()) return;
        await ConvertLinkType(entry, LinkType.DirectoryLink);
    }

    /// <summary> 转换链接类型：弹出预览确认，成功后更新索引和 UI。 </summary>
    private async Task ConvertLinkType(LinkEntry entry, LinkType newType)
    {
        var vm = new ConversionPreviewViewModel { Entry = entry, NewType = newType };
        var dialog = new ConversionPreviewDialog { DataContext = vm };
        dialog.Owner = Application.Current.MainWindow;
        if (dialog.ShowDialog() != true) return;

        try
        {
            var result = _linkService.ConvertType(entry.LinkPath, entry.LinkType, newType, entry.TargetPath);
            if (result.Success)
            {
                var actualType = _linkService.DetectType(entry.LinkPath);
                if (actualType != newType)
                {
                    MessageBox.Show($"转换后类型校验失败：请求 {newType}，实际 {actualType}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                entry.LinkType = actualType;
                await _indexService.UpsertAsync(entry);
                _uiContext.Post(_ => LinksView.Refresh(), null);
                StatusMessage = $"已转换: {entry.LinkName}";
            }
            else
            {
                MessageBox.Show($"转换失败: {result.ErrorMessage}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "转换链接类型失败: {LinkPath}", entry.LinkPath);
            MessageBox.Show($"转换失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary> 复制链接路径到剪贴板。 </summary>
    private void CopyLinkPath()
    {
        if (SelectedLink is not null)
            Clipboard.SetText(SelectedLink.LinkPath);
    }

    /// <summary> 复制目标路径到剪贴板。 </summary>
    private void CopyTargetPath()
    {
        if (SelectedLink is not null)
            Clipboard.SetText(SelectedLink.TargetPath);
    }

    /// <summary> 添加选中链接到白名单并更新索引。 </summary>
    private async void AddToWhitelist()
    {
        var entry = SelectedLink;
        if (entry is null) return;
        await _whitelistService.AddManualAsync(entry.LinkPath);
        entry.InWhitelist = true;
        await _indexService.UpsertAsync(entry);
        _uiContext.Post(_ => LinksView.Refresh(), null);
        StatusMessage = $"已添加到白名单: {entry.LinkName}";
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary> 从白名单移除选中链接并更新索引。 </summary>
    private async void RemoveFromWhitelist()
    {
        var entry = SelectedLink;
        if (entry is null) return;
        await _whitelistService.RemoveAsync(entry.LinkPath);
        entry.InWhitelist = false;
        await _indexService.UpsertAsync(entry);
        _uiContext.Post(_ => LinksView.Refresh(), null);
        StatusMessage = $"已从白名单移除: {entry.LinkName}";
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary> 创建新链接：弹出创建对话框，成功后自动加入白名单。 </summary>
    /// <summary> 创建新链接：弹出创建对话框，成功后自动加入白名单。 </summary>
    private async void CreateNewLink()
    {
        var vm = new CreateLinkViewModel(_linkService, _indexService);
        var dialog = new CreateLinkDialog { DataContext = vm };
        dialog.Owner = Application.Current.MainWindow;
        if (dialog.ShowDialog() != true) return;

        var entry = vm.CreatedEntry;
        if (entry is null) return;

        // 新创建的链接自动加入白名单
        await _whitelistService.AddAutoAsync(entry.LinkPath);
        entry.InWhitelist = true;
        await _indexService.UpsertAsync(entry);

        _uiContext.Post(_ => Links.Add(entry), null);
        StatusMessage = $"已创建: {entry.LinkName}";
    }

    // ── 编辑链接 ──
    /// <summary> 编辑选中链接：弹出编辑对话框，路径改变时清理旧索引。 </summary>
    private async void EditLink()
    {
        var entry = SelectedLink;
        if (entry is null) return;

        var vm = new CreateLinkViewModel(_linkService, _indexService, entry);
        var dialog = new CreateLinkDialog { DataContext = vm };
        dialog.Owner = Application.Current.MainWindow;
        if (dialog.ShowDialog() != true) return;

        var updated = vm.CreatedEntry;
        if (updated is null) return;

        // 路径变更时从索引中移除旧记录
        if (entry.LinkPath != updated.LinkPath)
            await _indexService.DeleteAsync(entry.LinkPath);

        await _indexService.UpsertAsync(updated);
        _uiContext.Post(_ => { Links.Remove(entry); Links.Add(updated); }, null);
        StatusMessage = $"已更新: {updated.LinkName}";
    }

    // ── 打开目标位置 ──
    /// <summary> 在资源管理器中打开链接的目标路径（支持相对路径解析）。 </summary>
    private void OpenTargetLocation()
    {
        var entry = SelectedLink;
        if (entry is null) return;

        var target = entry.TargetPath;
        if (string.IsNullOrWhiteSpace(target)) return;

        if (!Path.IsPathRooted(target))
        {
            var folder = Path.GetDirectoryName(entry.LinkPath);
            if (!string.IsNullOrEmpty(folder))
                target = Path.GetFullPath(Path.Combine(folder, target));
        }

        if (Directory.Exists(target))
            System.Diagnostics.Process.Start("explorer.exe", target);
        else
        {
            // 目标本身不存在时，尝试打开其父目录
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                System.Diagnostics.Process.Start("explorer.exe", parent);
        }
    }

    // ── 手动刷新（保留选中项，原地更新） ──
    /// <summary> 手动刷新：从索引重新加载所有链接并更新状态。 </summary>
    private async Task RefreshAsync()
    {
        if (IsScanning) return;
        StatusMessage = "正在刷新索引...";
        var links = await _indexService.GetAllAsync();
        await RefreshEntryStatusesAsync(links);
        _uiContext.Post(_ => SyncLinksWithDb(links), null);
        StatusMessage = $"已刷新 {Links.Count} 个符号链接";
    }

    // ── 自动刷新：仅检查受影响的条目 ──
    /// <summary> 文件系统变更后自动刷新，仅校验受影响路径的条目以节省 I/O。 </summary>
    private async Task PerformAutoRefreshAsync(IReadOnlyCollection<string> affectedPaths)
    {
        try
        {
            var allEntries = await _indexService.GetAllAsync();

            // 仅校验受变更影响的条目（避免全量 I/O）
            if (affectedPaths.Count > 0)
            {
                var affectedSet = new HashSet<string>(affectedPaths, StringComparer.OrdinalIgnoreCase);
                var relevantEntries = allEntries
                    .Where(e => affectedSet.Contains(e.LinkPath) || affectedSet.Contains(e.TargetPath))
                    .ToList();
                await RefreshEntryStatusesAsync(relevantEntries);
            }

            _uiContext.Post(_ => SyncLinksWithDb(allEntries), null);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "自动刷新失败"); }
    }

    // ── 核心同步：用 DB 数据原地更新 Links 集合，不替换已有对象 ──
    /// <summary> 用数据库快照原地同步 Links 集合：增删改均通过现有对象属性更新，保持 UI 绑定稳定。 </summary>
    private void SyncLinksWithDb(List<LinkEntry> dbEntries)
    {
        // O(1) 查找：构建现有条目的 Dictionary
        var existingDict = new Dictionary<string, LinkEntry>(Links.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var link in Links)
            existingDict[link.LinkPath] = link;

        // DB 路径集合
        var dbPathSet = new HashSet<string>(dbEntries.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var e in dbEntries)
            dbPathSet.Add(e.LinkPath);

        var changed = false;

        // 移除已不在 DB 中的条目
        for (int i = Links.Count - 1; i >= 0; i--)
        {
            if (!dbPathSet.Contains(Links[i].LinkPath))
            {
                Links.RemoveAt(i);
                changed = true;
            }
        }

        // 更新已有条目的属性 & 添加全新条目
        foreach (var entry in dbEntries)
        {
            if (existingDict.TryGetValue(entry.LinkPath, out var existing))
            {
                // 只更新发生变化的属性，避免不必要地刷新 UI
                if (existing.Status != entry.Status ||
                    existing.LastSeenTime != entry.LastSeenTime ||
                    existing.LinkType != entry.LinkType ||
                    existing.TargetPath != entry.TargetPath)
                {
                    existing.Status = entry.Status;
                    existing.LastSeenTime = entry.LastSeenTime;
                    existing.LinkType = entry.LinkType;
                    existing.TargetPath = entry.TargetPath;
                    changed = true;
                }
            }
            else
            {
                Links.Add(entry);
                changed = true;
            }
        }

        if (changed)
            StatusMessage = $"已检测到变更，刷新完成 - {Links.Count} 个链接";
    }

    /// <summary> 批量校验链接状态：遍历条目检查目标是否存在，更新不一致的状态。 </summary>
    private async Task RefreshEntryStatusesAsync(IEnumerable<LinkEntry> entries)
    {
        foreach (var entry in entries)
        {
            var isValid = IsTargetValid(entry);
            var expectedStatus = isValid ? LinkStatus.Valid : LinkStatus.Broken;
            if (entry.Status != expectedStatus)
            {
                entry.Status = expectedStatus;
                await _indexService.UpsertAsync(entry);
            }
        }
    }

    // ── 低频率健康检查：每 5 分钟校验所有链接目标是否仍然存在 ──
    /// <summary> 定时健康检查，每 5 分钟扫描一次全部链接状态并及时更新。 </summary>
    private async Task PeriodicHealthCheckAsync()
    {
        if (IsScanning) return;
        try
        {
            var entries = await _indexService.GetAllAsync();
            var changedCount = 0;
            foreach (var entry in entries)
            {
                var isValid = IsTargetValid(entry);
                var expectedStatus = isValid ? LinkStatus.Valid : LinkStatus.Broken;
                if (entry.Status != expectedStatus)
                {
                    entry.Status = expectedStatus;
                    await _indexService.UpsertAsync(entry);
                    changedCount++;
                }
            }
            if (changedCount > 0)
                _uiContext.Post(_ =>
                {
                    StatusMessage = $"健康检查: {changedCount} 个链接状态已更新";
                }, null);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "健康检查失败"); }
    }

    /// <summary> 判断链接的目标路径是否存在（支持绝对和相对路径）。 </summary>
    private static bool IsTargetValid(LinkEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.TargetPath)) return false;
        var target = entry.TargetPath;
        // 相对路径基于链接位置解析
        if (!Path.IsPathRooted(target))
        {
            var folder = Path.GetDirectoryName(entry.LinkPath);
            if (!string.IsNullOrEmpty(folder))
                target = Path.GetFullPath(Path.Combine(folder, target));
        }
        return Directory.Exists(target) || File.Exists(target);
    }

    // ── 自动刷新（文件系统变更检测） ──
    /// <summary> USN 变更事件处理：防抖 500ms 后执行自动刷新，仅处理受影响路径。 </summary>
    private void OnFileChangeDetected(object? sender, FsChangeEventArgs e)
    {
        if (IsScanning) return;
        _refreshDebounceCts?.Cancel();
        _refreshDebounceCts = new CancellationTokenSource();
        var token = _refreshDebounceCts.Token;
        var affectedPaths = e.AffectedPaths ?? (IReadOnlyCollection<string>)new[] { e.Path };
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, token);
                await PerformAutoRefreshAsync(affectedPaths);
            }
            catch (TaskCanceledException) { }
        }, token);
    }

    // ── 批量白名单 ──
    /// <summary> 判断是否可批量添加到白名单（至少有一个不在白名单中的选中项）。 </summary>
    private bool CanAddSelectedToWhitelist()
        => SelectedLinks != null && SelectedLinks.Count > 0 && SelectedLinks.Any(e => !e.InWhitelist);

    /// <summary> 批量添加选中链接到白名单。 </summary>
    private async Task AddSelectedToWhitelist()
    {
        var entries = SelectedLinks?.Where(e => !e.InWhitelist).ToList();
        if (entries is null || entries.Count == 0) return;
        foreach (var e in entries)
        {
            await _whitelistService.AddManualAsync(e.LinkPath);
            e.InWhitelist = true;
            await _indexService.UpsertAsync(e);
        }
        _uiContext.Post(_ => LinksView.Refresh(), null);
        StatusMessage = $"已将 {entries.Count} 个链接加入白名单";
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary> 判断是否可批量从白名单移除（至少有一个在白名单中的选中项）。 </summary>
    private bool CanRemoveSelectedFromWhitelist()
        => SelectedLinks != null && SelectedLinks.Count > 0 && SelectedLinks.Any(e => e.InWhitelist);

    /// <summary> 批量从白名单移除选中链接。 </summary>
    private async Task RemoveSelectedFromWhitelist()
    {
        var entries = SelectedLinks?.Where(e => e.InWhitelist).ToList();
        if (entries is null || entries.Count == 0) return;
        foreach (var e in entries)
        {
            await _whitelistService.RemoveAsync(e.LinkPath);
            e.InWhitelist = false;
            await _indexService.UpsertAsync(e);
        }
        _uiContext.Post(_ => LinksView.Refresh(), null);
        StatusMessage = $"已将 {entries.Count} 个链接从白名单移除";
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary> 打开设置对话框。 </summary>
    private static void OpenSettings()
    {
        var vm = App.Services.GetRequiredService<SettingsViewModel>();
        var dialog = new SettingsDialog { DataContext = vm };
        dialog.Owner = Application.Current.MainWindow;
        dialog.ShowDialog();
    }
}
