using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using SymlinkManager.Core.Data;
using SymlinkManager.Core.Services;
using SymlinkManager.App.ViewModels;
using SymlinkManager.App.Views;

namespace SymlinkManager.App;

public partial class App : Application
{
    private readonly CancellationTokenSource _appCts = new();
    private ServiceProvider? _services;
    internal static ServiceProvider Services => ((App)Current)._services!;

    // 持久化目录：固定用 %LocalAppData%\SymlinkManager （开发/发布一致）
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SymlinkManager");
    public static string ConfigDir => Path.Combine(DataDir, "config");
    public static string ConfigFile => Path.Combine(ConfigDir, "app.config");

    public static string DefaultDatabasePath => Path.Combine(DataDir, "symlink-manager.db");
    public static string LogDir => Path.Combine(DataDir, "logs");

    public static AppConfig LoadConfig()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            if (File.Exists(ConfigFile))
            {
                var json = File.ReadAllText(ConfigFile);
                return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
        }
        catch { }
        return new AppConfig();
    }

    public static void SaveConfig(AppConfig config)
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigFile, json);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!IsAdministrator())
        {
            var result = MessageBox.Show(
                "SymlinkManager 需要管理员权限才能扫描 NTFS 卷。\n\n是否以管理员身份重新启动？",
                "需要管理员权限",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (result == MessageBoxResult.Yes)
            {
                var exePath = Environment.ProcessPath!;
                var psi = new ProcessStartInfo(exePath)
                {
                    UseShellExecute = true,
                    Verb = "runas"
                };
                try { Process.Start(psi); } catch { }
            }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show($"UI 异常: {ex.Exception.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        try
        {
            var config = LoadConfig();

            Directory.CreateDirectory(LogDir);
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(Path.Combine(LogDir, "app-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30)
                .CreateLogger();

            var services = new ServiceCollection();
            services.AddLogging(b => b.AddSerilog(dispose: true));

            var dbPath = string.IsNullOrWhiteSpace(config.DatabasePath)
                ? DefaultDatabasePath
                : config.DatabasePath;
            services.AddSingleton(new AppDbContext($"Data Source={dbPath}"));

            services.AddSingleton<IIndexService, IndexService>();
            services.AddSingleton<IVolumeHandleService, VolumeHandleService>();
            services.AddSingleton<VolumeHandleService>();
            services.AddSingleton<ISymlinkService, SymlinkService>();
            services.AddSingleton<IWhitelistService, WhitelistService>();
            services.AddSingleton<IScannerService, MftScannerService>();
            services.AddSingleton<IUsnMonitorService, UsnMonitorService>();

            services.AddSingleton<MainViewModel>();
            services.AddSingleton<MainWindow>();

            services.AddTransient<CreateLinkViewModel>();
            services.AddTransient<ConversionPreviewViewModel>();
            services.AddTransient<DeleteConfirmViewModel>();
            services.AddTransient<SettingsViewModel>(sp =>
                new SettingsViewModel(sp.GetRequiredService<IIndexService>()));

            _services = services.BuildServiceProvider();

            var mainWindow = _services.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;
            mainWindow.Show();

            _ = InitializeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动失败: {ex}", "致命错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private async Task InitializeAsync()
    {
        if (_services == null) return;
        try
        {
            var logger = _services.GetRequiredService<ILogger<App>>();

            logger.LogInformation("数据目录: {Dir}", DataDir);
            logger.LogInformation("初始化数据库...");
            var db = _services.GetRequiredService<AppDbContext>();
            await db.InitializeAsync();

            logger.LogInformation("加载索引...");
            var idx = _services.GetRequiredService<IIndexService>();
            await idx.InitializeAsync();
            idx.StartBatchPersist(_appCts.Token);

            logger.LogInformation("打开卷句柄...");
            var vol = _services.GetRequiredService<IVolumeHandleService>();
            vol.OpenVolume();

            logger.LogInformation("加载扫描目录...");
            _ = await idx.GetScanDirectoriesAsync();

            logger.LogInformation("启动 USN 监控...");
            var usn = _services.GetRequiredService<IUsnMonitorService>();
            await usn.StartAsync(_appCts.Token);

            logger.LogInformation("初始化完成");

            await Dispatcher.InvokeAsync(async () =>
            {
                var mainVm = _services.GetRequiredService<MainViewModel>();
                await mainVm.InitializeAsync();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                MessageBox.Show($"初始化失败: {ex.Message}\n\n{ex.StackTrace}",
                    "初始化错误", MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _appCts.Cancel();
        if (_services != null)
        {
            var usn = _services.GetRequiredService<IUsnMonitorService>();
            await usn.StopAsync();
        }
        _appCts.Dispose();
        _services?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}

public class AppConfig
{
    public string? DatabasePath { get; set; }
}
