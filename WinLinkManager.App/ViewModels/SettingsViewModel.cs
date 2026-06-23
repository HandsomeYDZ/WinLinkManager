using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using WinLinkManager.App.ViewModels.Base;
using WinLinkManager.Core.Models;
using WinLinkManager.Core.Services;

namespace WinLinkManager.App.ViewModels;

/// <summary>
/// 设置界面的 ViewModel，管理扫描目录列表和数据库路径配置。
/// </summary>
public class SettingsViewModel : ViewModelBase
{
    private readonly IIndexService _indexService;

    public ObservableCollection<ScanDirectoryItem> ScanDirectories { get; } = new();

    private string _configPath;
    public string ConfigPath
    {
        get => _configPath;
        set => SetProperty(ref _configPath, value);
    }

    private string _databasePath;
    public string DatabasePath
    {
        get => _databasePath;
        set => SetProperty(ref _databasePath, value);
    }

    public string ConfigDirPath => App.ConfigDir;
    public string DataDirPath => App.DataDir;

    public ICommand AddScanDirCommand { get; }
    public ICommand RemoveScanDirCommand { get; }
    public ICommand ExcludeScanDirCommand { get; }
    public ICommand ChangeDatabasePathCommand { get; }
    public ICommand OpenConfigFolderCommand { get; }
    public ICommand OpenDataFolderCommand { get; }

    /// <summary> 无参构造用于设计时或手动创建，不加载数据。 </summary>
    public SettingsViewModel()
    {
        _indexService = null!;
        _configPath = App.ConfigFile;
        _databasePath = App.LoadConfig().DatabasePath ?? App.DefaultDatabasePath;

        AddScanDirCommand = new RelayCommand(AddScanDir);
        RemoveScanDirCommand = new RelayCommand(RemoveScanDir, () => SelectedDir is not null);
        ExcludeScanDirCommand = new RelayCommand(ToggleExclude, () => SelectedDir is not null);
        ChangeDatabasePathCommand = new RelayCommand(ChangeDatabasePath);
        OpenConfigFolderCommand = new RelayCommand(() => OpenFolder(App.ConfigDir));
        OpenDataFolderCommand = new RelayCommand(() => OpenFolder(App.DataDir));
    }

    /// <summary> DI 构造，注入 IIndexService 并异步加载扫描目录列表。 </summary>
    public SettingsViewModel(IIndexService indexService) : this()
    {
        _indexService = indexService;
        _ = LoadAsync();
    }

    /// <summary> 从索引服务加载扫描目录列表。 </summary>
    private async Task LoadAsync()
    {
        var dirs = await _indexService.GetScanDirectoriesAsync();
        ScanDirectories.Clear();
        foreach (var d in dirs)
            ScanDirectories.Add(new ScanDirectoryItem
            {
                Path = d.Path,
                IsExcluded = d.IsExcluded
            });
    }

    private ScanDirectoryItem? _selectedDir;
    public ScanDirectoryItem? SelectedDir
    {
        get => _selectedDir;
        set
        {
            if (SetProperty(ref _selectedDir, value))
            {
                ((RelayCommand)RemoveScanDirCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ExcludeScanDirCommand).RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary> 弹出文件夹选择对话框添加新的扫描目录，重复路径跳过。 </summary>
    private void AddScanDir()
    {
        string path;
        using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
        {
            dialog.Description = "选择要扫描的目录";
            dialog.ShowNewFolderButton = true;
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            path = dialog.SelectedPath;
        }
        if (ScanDirectories.Any(d => d.Path == path)) return;

        ScanDirectories.Add(new ScanDirectoryItem { Path = path, IsExcluded = false });
        _ = SaveAsync();
    }

    /// <summary> 从列表中移除选中的扫描目录并持久化。 </summary>
    private void RemoveScanDir()
    {
        if (SelectedDir is null) return;
        ScanDirectories.Remove(SelectedDir);
        _ = SaveAsync();
    }

    /// <summary> 切换选中目录的排除/扫描标记。 </summary>
    private void ToggleExclude()
    {
        if (SelectedDir is null) return;
        SelectedDir.IsExcluded = !SelectedDir.IsExcluded;
        _ = SaveAsync();
    }

    /// <summary> 将当前扫描目录列表保存到索引服务。 </summary>
    private async Task SaveAsync()
    {
        var configs = ScanDirectories.Select(d => new ScanDirectoryConfig
        {
            Path = d.Path,
            IsExcluded = d.IsExcluded,
            AddedTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        }).ToList();
        await _indexService.SaveScanDirectoriesAsync(configs);
    }

    /// <summary> 选择新的数据库文件位置，复制旧数据并保存配置。 </summary>
    private void ChangeDatabasePath()
    {
        var dialog = new SaveFileDialog
        {
            Title = "选择数据库文件位置",
            Filter = "SQLite 数据库 (*.db)|*.db|All files (*.*)|*.*",
            FileName = Path.GetFileName(DatabasePath),
            InitialDirectory = Path.GetDirectoryName(DatabasePath) ?? App.DataDir
        };

        if (dialog.ShowDialog() != true) return;
        var newPath = dialog.FileName;
        try
        {
            var newDir = Path.GetDirectoryName(newPath)!;
            Directory.CreateDirectory(newDir);
            // 复制现有数据库到新位置
            if (File.Exists(DatabasePath))
            {
                File.Copy(DatabasePath, newPath, overwrite: true);
            }

            DatabasePath = newPath;
            App.SaveConfig(new AppConfig { DatabasePath = DatabasePath });
            MessageBox.Show($"数据库路径已保存，重启后生效:\n{DatabasePath}", "保存成功",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败: {ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary> 在资源管理器中打开指定文件夹。 </summary>
    private static void OpenFolder(string path)
    {
        if (Directory.Exists(path))
            Process.Start("explorer.exe", path);
    }

    public string AppVersion => "WinLink Manager v1.0.2";
    public string Description => "Windows 符号链接管理器 — 扫描、管理、监控文件系统中的符号链接和交接点。";
}

/// <summary>
/// 扫描目录列表中的单个条目，含路径和排除状态。
/// </summary>
public class ScanDirectoryItem : ViewModelBase
{
    private string _path = "";
    public string Path
    {
        get => _path;
        set => SetProperty(ref _path, value);
    }

    private bool _isExcluded;
    public bool IsExcluded
    {
        get => _isExcluded;
        set
        {
            if (SetProperty(ref _isExcluded, value))
                OnPropertyChanged(nameof(ExcludeLabel));
        }
    }

    public string ExcludeLabel => IsExcluded ? "排除" : "扫描";
}
