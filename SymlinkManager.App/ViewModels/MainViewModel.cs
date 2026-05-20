using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SymlinkManager.App.ViewModels.Base;
using SymlinkManager.App.Views;
using SymlinkManager.Core.Models;
using SymlinkManager.Core.Services;

namespace SymlinkManager.App.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly IIndexService _indexService;
    private readonly ISymlinkService _symlinkService;
    private readonly IWhitelistService _whitelistService;
    private readonly IScannerService _scannerService;
    private readonly ILogger<MainViewModel> _logger;
    private readonly SynchronizationContext _uiContext;
    private CancellationTokenSource? _searchDebounceCts;

    public ObservableCollection<SymlinkEntry> Links { get; } = new();

    private ICollectionView? _linksView;
    public ICollectionView LinksView =>
        _linksView ??= CollectionViewSource.GetDefaultView(Links);

    // ── 标签切换 ──
    private bool _showAllLinks = true;
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
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                DebounceSearch();
        }
    }

    // ── 扫描状态 ──
    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        set
        {
            if (SetProperty(ref _isScanning, value))
            {
                ((RelayCommand)ScanOrRebuildCommand).RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(ScanButtonLabel));
            }
        }
    }

    private ScanProgress? _scanProgress;
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
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string ScanProgressText =>
        ScanProgress is null ? "" :
        ScanProgress.IsComplete ? "扫描完成" :
        $"当前: {ScanProgress.CurrentDirectory ?? "..."}  (已扫 {ScanProgress.TotalScanned:N0} 项, 找到 {ScanProgress.LinksFound} 个链接)";

    public string ScanButtonLabel =>
        IsScanning ? "扫描中..." :
        Links.Count == 0 ? "建立索引" : "重建索引";

    private SymlinkEntry? _selectedLink;
    public SymlinkEntry? SelectedLink
    {
        get => _selectedLink;
        set
        {
            if (SetProperty(ref _selectedLink, value))
            {
                ((RelayCommand)OpenLocationCommand).RaiseCanExecuteChanged();
                ((RelayCommand)DeleteLinkCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ConvertToJunctionCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ConvertToDirectorySymlinkCommand).RaiseCanExecuteChanged();
                ((RelayCommand)CopyLinkPathCommand).RaiseCanExecuteChanged();
                ((RelayCommand)CopyTargetPathCommand).RaiseCanExecuteChanged();
                ((RelayCommand)AddToWhitelistCommand).RaiseCanExecuteChanged();
                ((RelayCommand)RemoveFromWhitelistCommand).RaiseCanExecuteChanged();
            }
        }
    }

    // ── 命令 ──
    public ICommand OpenLocationCommand { get; }
    public ICommand DeleteLinkCommand { get; }
    public ICommand ConvertToJunctionCommand { get; }
    public ICommand ConvertToDirectorySymlinkCommand { get; }
    public ICommand CopyLinkPathCommand { get; }
    public ICommand CopyTargetPathCommand { get; }
    public ICommand AddToWhitelistCommand { get; }
    public ICommand RemoveFromWhitelistCommand { get; }
    public ICommand CreateNewLinkCommand { get; }
    public ICommand ScanOrRebuildCommand { get; }
    public ICommand OpenSettingsCommand { get; }

    public MainViewModel(
        IIndexService indexService,
        ISymlinkService symlinkService,
        IWhitelistService whitelistService,
        IScannerService scannerService,
        ILogger<MainViewModel> logger)
    {
        _indexService = indexService;
        _symlinkService = symlinkService;
        _whitelistService = whitelistService;
        _scannerService = scannerService;
        _logger = logger;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();

        OpenLocationCommand = new RelayCommand(OpenLocation, () => SelectedLink is not null);
        DeleteLinkCommand = new RelayCommand(DeleteLink, () => SelectedLink is not null);
        ConvertToJunctionCommand = new RelayCommand(ConvertToJunction, () => SelectedLink is not null && CanConvertToJunction());
        ConvertToDirectorySymlinkCommand = new RelayCommand(ConvertToDirectorySymlink, () => SelectedLink is not null && CanConvertToDirectorySymlink());
        CopyLinkPathCommand = new RelayCommand(CopyLinkPath, () => SelectedLink is not null);
        CopyTargetPathCommand = new RelayCommand(CopyTargetPath, () => SelectedLink is not null);
        AddToWhitelistCommand = new RelayCommand(AddToWhitelist, () => SelectedLink is not null && !SelectedLink.InWhitelist);
        RemoveFromWhitelistCommand = new RelayCommand(RemoveFromWhitelist, () => SelectedLink is not null && SelectedLink.InWhitelist);
        CreateNewLinkCommand = new RelayCommand(CreateNewLink);
        ScanOrRebuildCommand = new RelayCommand(async () => await ScanOrRebuildAsync(), () => !IsScanning);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
    }

    public async Task InitializeAsync()
    {
        if (!await _indexService.HasExistingIndexAsync())
        {
            await RunScanAsync("正在建立首次索引...");
        }
        else
        {
            var links = await _indexService.GetAllAsync();
            foreach (var link in links)
                Links.Add(link);
            StatusMessage = $"已加载 {Links.Count} 个符号链接";
        }

        LinksView.Filter = o => FilterLink(o as SymlinkEntry);
        LinksView.SortDescriptions.Add(new SortDescription(nameof(SymlinkEntry.LinkName), ListSortDirection.Ascending));
    }

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

    private async Task RunScanAsync(string initialMessage)
    {
        IsScanning = true;
        StatusMessage = initialMessage;

        try
        {
            var scanDirs = await _indexService.GetScanDirectoriesAsync();
            var progress = new Progress<ScanProgress>(p =>
            {
                _uiContext.Post(_ =>
                {
                    ScanProgress = p;
                    StatusMessage = $"扫描中... 已扫描 {p.TotalScanned:N0} 项, 发现 {p.LinksFound} 个链接";
                }, null);
            });

            await foreach (var entry in _scannerService.FullScanAsync(scanDirs, progress, CancellationToken.None))
            {
                await _indexService.UpsertAsync(entry);
                _uiContext.Post(_ => Links.Add(entry), null);
            }
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
    private bool FilterLink(SymlinkEntry? entry)
    {
        if (entry is null) return false;

        // Tab 过滤
        if (_showWhitelistOnly && !entry.InWhitelist)
            return false;

        // 搜索文本过滤
        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            var text = _searchText.ToLowerInvariant();
            return entry.LinkName.ToLowerInvariant().Contains(text) ||
                   entry.LinkPath.ToLowerInvariant().Contains(text) ||
                   entry.TargetPath.ToLowerInvariant().Contains(text);
        }

        return true;
    }

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
    private void OpenLocation()
    {
        var entry = SelectedLink;
        if (entry is null) return;
        var dir = Path.GetDirectoryName(entry.LinkPath);
        if (!string.IsNullOrEmpty(dir))
            System.Diagnostics.Process.Start("explorer.exe", dir);
    }

    private async void DeleteLink()
    {
        var entry = SelectedLink;
        if (entry is null) return;

        var vm = new DeleteConfirmViewModel { Entry = entry };
        var dialog = new DeleteConfirmDialog { DataContext = vm };
        if (dialog.ShowDialog() != true) return;

        try
        {
            _symlinkService.DeleteSymlink(entry.LinkPath, entry.LinkType);
            _uiContext.Post(_ => Links.Remove(entry), null);
            StatusMessage = $"已删除: {entry.LinkName}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除链接失败: {LinkPath}", entry.LinkPath);
            MessageBox.Show($"删除失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool CanConvertToJunction()
        => SelectedLink is not null && SelectedLink.LinkType != SymlinkType.Junction && SelectedLink.LinkType != SymlinkType.FileSymlink;

    private async void ConvertToJunction()
    {
        var entry = SelectedLink;
        if (entry is null || !CanConvertToJunction()) return;
        await ConvertLinkType(entry, SymlinkType.Junction);
    }

    private bool CanConvertToDirectorySymlink()
        => SelectedLink is not null && SelectedLink.LinkType is not SymlinkType.DirectorySymlink and not SymlinkType.FileSymlink;

    private async void ConvertToDirectorySymlink()
    {
        var entry = SelectedLink;
        if (entry is null || !CanConvertToDirectorySymlink()) return;
        await ConvertLinkType(entry, SymlinkType.DirectorySymlink);
    }

    private async Task ConvertLinkType(SymlinkEntry entry, SymlinkType newType)
    {
        var vm = new ConversionPreviewViewModel { Entry = entry, NewType = newType };
        var dialog = new ConversionPreviewDialog { DataContext = vm };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var result = _symlinkService.ConvertType(entry.LinkPath, entry.LinkType, newType, entry.TargetPath);
            if (result.Success)
            {
                entry.LinkType = newType;
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

    private void CopyLinkPath()
    {
        if (SelectedLink is not null)
            Clipboard.SetText(SelectedLink.LinkPath);
    }

    private void CopyTargetPath()
    {
        if (SelectedLink is not null)
            Clipboard.SetText(SelectedLink.TargetPath);
    }

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

    private void CreateNewLink()
    {
        var vm = new CreateLinkViewModel(_symlinkService, _indexService);
        var dialog = new CreateLinkDialog { DataContext = vm };
        if (dialog.ShowDialog() != true) return;

        var entry = vm.CreatedEntry;
        if (entry is null) return;

        _uiContext.Post(_ => Links.Add(entry), null);
        StatusMessage = $"已创建: {entry.LinkName}";
    }

    private static void OpenSettings()
    {
        var vm = App.Services.GetRequiredService<SettingsViewModel>();
        var dialog = new SettingsDialog { DataContext = vm };
        dialog.ShowDialog();
    }
}
