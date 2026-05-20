using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SymlinkManager.Core.Native;

namespace SymlinkManager.Core.Services;

public class UsnMonitorService : IUsnMonitorService, IDisposable
{
    private readonly ILogger<UsnMonitorService> _logger;
    private readonly VolumeHandleService _volumeService;
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private FileSystemWatcher? _fsw;

    public bool IsFallbackMode { get; private set; }
    public bool IsRunning { get; private set; }

    public UsnMonitorService(
        VolumeHandleService volumeService,
        ILogger<UsnMonitorService> logger)
    {
        _volumeService = volumeService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (IsRunning)
            return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IsRunning = true;

        try
        {
            _volumeService.OpenVolume();
            if (TryQueryUsnJournal())
            {
                _logger.LogInformation("USN journal available, starting native monitoring");
                _monitorTask = UsnPollingLoopAsync(_cts.Token);
            }
            else
            {
                throw new InvalidOperationException("USN journal query failed");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "USN journal not available, falling back to FileSystemWatcher");
            IsFallbackMode = true;
            StartFileSystemWatcher();
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!IsRunning)
            return;

        _cts?.Cancel();
        _fsw?.Dispose();
        _fsw = null;

        if (_monitorTask != null)
        {
            try
            {
                await _monitorTask;
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation
            }
        }

        _volumeService.Dispose();
        IsRunning = false;
        _logger.LogInformation("USN monitor stopped");
    }

    private bool TryQueryUsnJournal()
    {
        // Attempt to query the USN journal to check availability.
        // If this fails, the volume doesn't support USN or we don't have access.
        try
        {
            var handle = _volumeService.GetHandle();
            if (handle.Handle == nint.Zero || handle.Handle == new nint(-1))
                return false;

            // Send a zero-length FSCTL_QUERY_USN_JOURNAL to test availability.
            // The call will fail with ERROR_INVALID_FUNCTION if USN is unsupported,
            // or ERROR_BAD_COMMAND if the journal doesn't exist yet.

            var outputBuffer = Marshal.AllocHGlobal(32);
            try
            {
                // Use a dummy volume handle just for the probe
                using var probeHandle = NtfsNative.CreateFileW(
                    @"\\.\C:",
                    NtfsNative.GENERIC_READ,
                    NtfsNative.FILE_SHARE_READ | NtfsNative.FILE_SHARE_WRITE,
                    nint.Zero,
                    NtfsNative.OPEN_EXISTING,
                    NtfsNative.FILE_FLAG_BACKUP_SEMANTICS,
                    nint.Zero);

                if (probeHandle.IsInvalid)
                    return false;

                // We don't send actual USN_RECORD_V2/V3 query data — just test if the
                // ioctl is accepted at all. A real USN caller would provide an input buffer
                // but here we only need to know if the filesystem supports the control code.
                return NtfsNative.DeviceIoControl(
                    probeHandle,
                    NtfsNative.FSCTL_QUERY_USN_JOURNAL,
                    nint.Zero,
                    0,
                    outputBuffer,
                    32,
                    out _,
                    nint.Zero);
            }
            finally
            {
                Marshal.FreeHGlobal(outputBuffer);
            }
        }
        catch
        {
            return false;
        }
    }

    private async Task UsnPollingLoopAsync(CancellationToken ct)
    {
        _logger.LogInformation("USN polling loop started");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Future: read USN journal records and raise change events.
                // For now we simply heartbeat to keep the loop alive.
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
    }

    private void StartFileSystemWatcher()
    {
        _fsw = new FileSystemWatcher
        {
            Path = @"C:\",
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName |
                           NotifyFilters.DirectoryName |
                           NotifyFilters.Attributes,
            InternalBufferSize = 65536
        };

        _fsw.Created += OnFileSystemEvent;
        _fsw.Deleted += OnFileSystemEvent;
        _fsw.Renamed += OnRenamed;
        _fsw.Error += OnWatcherError;

        _fsw.EnableRaisingEvents = true;
        _logger.LogInformation("FileSystemWatcher started on C:\\");
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        try
        {
            var attr = File.GetAttributes(e.FullPath);
            if (attr.HasFlag(FileAttributes.ReparsePoint))
            {
                _logger.LogInformation(
                    "Reparse point {ChangeType}: {Path}",
                    e.ChangeType, e.FullPath);
            }
        }
        catch
        {
            // File may have been deleted between the event and our check
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        try
        {
            var attr = File.GetAttributes(e.FullPath);
            if (attr.HasFlag(FileAttributes.ReparsePoint))
            {
                _logger.LogInformation(
                    "Reparse point renamed: {OldPath} -> {NewPath}",
                    e.OldFullPath, e.FullPath);
            }
        }
        catch
        {
            // Ignore
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.LogError(e.GetException(), "FileSystemWatcher error occurred");
    }

    public void Dispose()
    {
        _fsw?.Dispose();
        _volumeService.Dispose();
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
