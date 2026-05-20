using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using SymlinkManager.App.ViewModels.Base;
using SymlinkManager.Core.Models;
using SymlinkManager.Core.Services;

namespace SymlinkManager.App.ViewModels;

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

    // DI constructor used at runtime
    public SettingsViewModel(IIndexService indexService) : this()
    {
        _indexService = indexService;
        _ = LoadAsync();
    }

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

    private void AddScanDir()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择要扫描的目录",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true) return;

        var path = dialog.FolderName;
        if (ScanDirectories.Any(d => d.Path == path)) return;

        ScanDirectories.Add(new ScanDirectoryItem { Path = path, IsExcluded = false });
        _ = SaveAsync();
    }

    private void RemoveScanDir()
    {
        if (SelectedDir is null) return;
        ScanDirectories.Remove(SelectedDir);
        _ = SaveAsync();
    }

    private void ToggleExclude()
    {
        if (SelectedDir is null) return;
        SelectedDir.IsExcluded = !SelectedDir.IsExcluded;
        _ = SaveAsync();
    }

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

    private static void OpenFolder(string path)
    {
        if (Directory.Exists(path))
            Process.Start("explorer.exe", path);
    }

    public string AppVersion => "Symlink Manager v1.0.0";
    public string Description => "Windows 符号链接管理器 — 扫描、管理、监控文件系统中的符号链接和交接点。";
}

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
