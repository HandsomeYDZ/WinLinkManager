using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using WinLinkManager.Core.Native;

namespace WinLinkManager.Core.Services;

/// <summary>
/// USN 日志监控服务：通过 USN 日志轮询 + FileSystemWatcher 双重机制检测文件变更
/// </summary>
public class UsnMonitorService : IUsnMonitorService, IDisposable
{
    private readonly ILogger<UsnMonitorService> _logger;
    private readonly IIndexService _indexService;
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private readonly List<FileSystemWatcher> _watchers = new(); // FileSystemWatcher 回退监控
    private long _lastUsn;        // 上次读取的 USN 序号
    private long _usnJournalId;   // 当前卷的 USN 日志 ID
    private bool _usnReady;       // USN journal 是否已就绪
    private ConcurrentDictionary<string, byte> _pendingChanges =
        new(StringComparer.OrdinalIgnoreCase); // 去重收集的待处理变更
    private readonly ConcurrentDictionary<string, byte> _knownLinkPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _debounceLock = new();
    private const int DebounceMs = 500;  // 变更事件防抖间隔
    private const int IdleBackoffMs = 250; // 防御性退避：即使驱动立即返回也不会空转

    /// <summary>文件系统变更事件</summary>
    public event EventHandler<FsChangeEventArgs>? ChangeDetected;

    /// <summary>是否正处于回退模式（FileSystemWatcher）</summary>
    public bool IsFallbackMode { get; private set; }
    /// <summary>监控是否正在运行</summary>
    public bool IsRunning { get; private set; }

    public UsnMonitorService(
        IIndexService indexService,
        ILogger<UsnMonitorService> logger)
    {
        _indexService = indexService;
        _logger = logger;
    }

    /// <summary>启动监控：初始化 USN journal，启动轮询，启动 FileSystemWatcher 辅助</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        if (IsRunning) return;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IsRunning = true;

        var scanDirs = await _indexService.GetScanDirectoriesAsync();
        var activePaths = scanDirs.Where(d => !d.IsExcluded && Directory.Exists(d.Path)).Select(d => d.Path).ToList();
        if (activePaths.Count == 0)
        {
            _logger.LogWarning("没有有效的扫描目录，跳过文件监控");
            IsRunning = false;
            return;
        }

        foreach (var entry in await _indexService.GetAllAsync())
            _knownLinkPaths[entry.LinkPath] = 0;

        try
        {
            if (TryInitUsnJournal())
            {
                _logger.LogInformation("USN journal 可用，启动 USN 轮询监控");
                _monitorTask = Task.Factory.StartNew(
                    () => UsnPollingLoop(_cts.Token),
                    _cts.Token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }
            else throw new InvalidOperationException("USN journal 不可用");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "USN journal 不可用，回退到 FileSystemWatcher");
            IsFallbackMode = true;
        }

        StartWatchers(activePaths);
        _logger.LogInformation("文件监控已启动，扫描目录: {Count} 个", activePaths.Count);
    }

    /// <summary>停止监控：取消轮询、清理 watcher、等待任务完成</summary>
    public async Task StopAsync()
    {
        if (!IsRunning) return;
        _cts?.Cancel();
        foreach (var fsw in _watchers) { fsw.EnableRaisingEvents = false; fsw.Dispose(); }
        _watchers.Clear();
        if (_monitorTask != null) { try { await _monitorTask; } catch (OperationCanceledException) { } }
        IsRunning = false;
        _logger.LogInformation("文件监控已停止");
    }

    /// <summary>尝试初始化 USN journal，读取当前 LastUsn 和 JournalId</summary>
    private bool TryInitUsnJournal()
    {
        try
        {
            var outputBuffer = Marshal.AllocHGlobal(64);
            try
            {
                using var probeHandle = NtfsNative.CreateFileW(@"\\.\C:", NtfsNative.GENERIC_READ,
                    NtfsNative.FILE_SHARE_READ | NtfsNative.FILE_SHARE_WRITE, IntPtr.Zero,
                    NtfsNative.OPEN_EXISTING, NtfsNative.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
                if (probeHandle.IsInvalid) return false;
                if (!NtfsNative.DeviceIoControl(probeHandle, NtfsNative.FSCTL_QUERY_USN_JOURNAL,
                    IntPtr.Zero, 0, outputBuffer, 64, out _, IntPtr.Zero)) return false;
                _usnJournalId = Marshal.ReadInt64(outputBuffer, 0);   // UsnJournalID
                _lastUsn = Marshal.ReadInt64(outputBuffer, 16);       // NextUsn
                _usnReady = true;
                return true;
            }
            finally { Marshal.FreeHGlobal(outputBuffer); }
        }
        catch { return false; }
    }

    /// <summary>USN 轮询主循环：阻塞式读取 USN journal 的变更记录</summary>
    private void UsnPollingLoop(CancellationToken ct)
    {
        _logger.LogInformation("USN 阻塞监听已启动 (NextUsn={LastUsn})", _lastUsn);

        using var volumeHandle = NtfsNative.CreateFileW(@"\\.\C:", NtfsNative.GENERIC_READ,
            NtfsNative.FILE_SHARE_READ | NtfsNative.FILE_SHARE_WRITE, IntPtr.Zero,
            NtfsNative.OPEN_EXISTING, NtfsNative.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (volumeHandle.IsInvalid)
        {
            _logger.LogWarning("无法打开卷句柄进行 USN 轮询");
            return;
        }

        using var cancellationRegistration = ct.Register(() =>
        {
            if (!volumeHandle.IsInvalid)
                NtfsNative.CancelIoEx(volumeHandle, IntPtr.Zero);
        });

        var inputBuffer = Marshal.AllocHGlobal(40);
        var outputBuffer = Marshal.AllocHGlobal(65536);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!_usnReady)
                {
                    if (ct.WaitHandle.WaitOne(1000)) break;
                    continue;
                }

                var result = CheckUsnJournalBlocking(volumeHandle, inputBuffer, outputBuffer);
                if (result == UsnReadResult.ReparseChanged)
                    FireEvent(FsChangeType.Modified, string.Empty);
                else if (result == UsnReadResult.Retry)
                {
                    if (ct.WaitHandle.WaitOne(1000)) break;
                }
                else
                {
                    if (ct.WaitHandle.WaitOne(IdleBackoffMs)) break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "USN 读取异常，停止 USN 监控");
        }
        finally
        {
            Marshal.FreeHGlobal(inputBuffer);
            Marshal.FreeHGlobal(outputBuffer);
        }
    }

    /// <summary>
    /// 阻塞式 USN 读取。BytesToWaitFor 非零时 DeviceIoControl 才会在
    /// 内核态等待；取消应用令牌时通过 CancelIoEx 唤醒同步 I/O。
    /// </summary>
    private enum UsnReadResult
    {
        NoChanges,
        ReparseChanged,
        Retry
    }

    private UsnReadResult CheckUsnJournalBlocking(
        SafeFileHandle volumeHandle,
        IntPtr inputBuf,
        IntPtr outputBuf)
    {
        const int inputSize = 40;
        const int outputSize = 65536;

        var reasonMask = NtfsNative.USN_REASON_FILE_CREATE |
                         NtfsNative.USN_REASON_FILE_DELETE |
                         NtfsNative.USN_REASON_RENAME_OLD_NAME |
                         NtfsNative.USN_REASON_RENAME_NEW_NAME |
                         NtfsNative.USN_REASON_REPARSE_POINT_CHANGE;

        Marshal.WriteInt64(inputBuf, 0, _lastUsn);               // StartUsn
        Marshal.WriteInt32(inputBuf, 8, unchecked((int)reasonMask));
        Marshal.WriteInt32(inputBuf, 12, 0);                     // ReturnOnlyOnClose
        Marshal.WriteInt64(inputBuf, 16, 1L);                    // Timeout（秒）
        Marshal.WriteInt64(inputBuf, 24, 1L);                    // 非零才会阻塞等待
        Marshal.WriteInt64(inputBuf, 32, _usnJournalId);

        try
        {
            if (!NtfsNative.DeviceIoControl(volumeHandle, NtfsNative.FSCTL_READ_USN_JOURNAL,
                    inputBuf, inputSize, outputBuf, outputSize, out var bytesReturned, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                // ERROR_HANDLE_EOF / ERROR_OPERATION_ABORTED 是正常的无数据或退出路径。
                if (error is 38 or 995) return UsnReadResult.NoChanges;
                _logger.LogWarning("FSCTL_READ_USN_JOURNAL 失败: error={Error}", error);
                return UsnReadResult.Retry;
            }

            if (bytesReturned < sizeof(long)) return UsnReadResult.Retry;

            // READ_USN_JOURNAL 输出的前 8 字节才是下一次读取的 StartUsn。
            _lastUsn = Marshal.ReadInt64(outputBuf, 0);
            var foundReparse = false;
            var offset = sizeof(long);

            while (offset + 8 <= bytesReturned)
            {
                var recordLen = Marshal.ReadInt32(outputBuf, offset);
                if (recordLen <= 0 || offset + recordLen > bytesReturned) break;

                var majorVersion = Marshal.ReadInt16(outputBuf, offset + 4);
                var reasonOffset = majorVersion == 3 ? 56 : 40;
                var attributesOffset = majorVersion == 3 ? 68 : 52;
                var minimumLength = majorVersion == 3 ? 76 : 60;

                if (recordLen >= minimumLength)
                {
                    var reason = unchecked((uint)Marshal.ReadInt32(outputBuf, offset + reasonOffset));
                    var attrs = unchecked((uint)Marshal.ReadInt32(outputBuf, offset + attributesOffset));
                    if ((reason & reasonMask) != 0 &&
                        (attrs & NtfsNative.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
                        foundReparse = true;
                }

                offset += recordLen;
            }

            return foundReparse ? UsnReadResult.ReparseChanged : UsnReadResult.NoChanges;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "解析 USN 记录失败");
            return UsnReadResult.Retry;
        }
    }

    /// <summary>在指定路径上启动 FileSystemWatcher 作为辅助监控</summary>
    private void StartWatchers(List<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                var fsw = new FileSystemWatcher
                {
                    Path = path, IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes,
                    InternalBufferSize = 65536
                };
                fsw.Created += OnWatcherEvent;
                fsw.Deleted += OnWatcherEvent;
                fsw.Changed += OnWatcherEvent;
                fsw.Renamed += OnWatcherRenamed;
                fsw.Error += OnWatcherError;
                fsw.EnableRaisingEvents = true;
                _watchers.Add(fsw);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "无法在 {Path} 上启动 FSW", path); }
        }
    }

    /// <summary>FSW 事件处理：仅关注重解析点，加入防抖队列</summary>
    private void OnWatcherEvent(object sender, FileSystemEventArgs e)
    {
        bool isReparse;
        if (e.ChangeType == WatcherChangeTypes.Deleted)
        {
            // 删除后无法再读取属性，只处理索引中已知的链接，避免每个普通文件删除都触发刷新。
            isReparse = _knownLinkPaths.TryRemove(e.FullPath, out _);
        }
        else
        {
            try
            {
                var attr = File.GetAttributes(e.FullPath);
                isReparse = attr.HasFlag(FileAttributes.ReparsePoint);
                if (isReparse) _knownLinkPaths[e.FullPath] = 0;
            }
            catch { isReparse = false; }
        }

        if (!isReparse) return;
        var type = e.ChangeType switch
        {
            WatcherChangeTypes.Created => FsChangeType.Created,
            WatcherChangeTypes.Deleted => FsChangeType.Deleted,
            _ => FsChangeType.Modified
        };
        _pendingChanges[e.FullPath] = 1;
        DebounceFire(type, e.FullPath);
    }

    /// <summary>FSW 重命名事件：同时记录新旧路径</summary>
    private void OnWatcherRenamed(object sender, RenamedEventArgs e)
    {
        var wasKnownLink = _knownLinkPaths.TryRemove(e.OldFullPath, out _);
        bool isReparse;
        try { var attr = File.GetAttributes(e.FullPath); isReparse = attr.HasFlag(FileAttributes.ReparsePoint); }
        catch { isReparse = false; }
        if (!isReparse && !wasKnownLink) return;
        if (isReparse) _knownLinkPaths[e.FullPath] = 0;
        _pendingChanges[e.FullPath] = 1;
        _pendingChanges[e.OldFullPath] = 1;
        DebounceFire(FsChangeType.Modified, e.FullPath);
    }

    /// <summary>FSW 错误日志</summary>
    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.LogError(e.GetException(), "FSW 错误");
    }

    /// <summary>防抖延迟触发事件，合并短时间内的多次变更</summary>
    private CancellationTokenSource? _debounceCts;
    private void DebounceFire(FsChangeType type, string path)
    {
        lock (_debounceLock)
        {
            var previous = _debounceCts;
            previous?.Cancel();
            _debounceCts = new CancellationTokenSource();
            previous?.Dispose();
            var token = _debounceCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(DebounceMs, token);
                    var snapshot = Interlocked.Exchange(ref _pendingChanges,
                        new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
                    if (snapshot.Count > 0)
                        FireEvent(type, path, snapshot.Keys.ToList());
                }
                catch (TaskCanceledException) { }
            }, token);
        }
    }

    /// <summary>触发变更事件通知订阅者</summary>
    private void FireEvent(FsChangeType type, string path, List<string>? affectedPaths = null)
    {
        try
        {
            ChangeDetected?.Invoke(this, new FsChangeEventArgs
            {
                ChangeType = type,
                Path = path,
                AffectedPaths = affectedPaths ?? new List<string> { path }
            });
        }
        catch (Exception ex) { _logger.LogWarning(ex, "事件处理器异常"); }
    }

    /// <summary>释放资源：停止所有 watcher 并取消后台任务</summary>
    public void Dispose()
    {
        foreach (var fsw in _watchers) { fsw.EnableRaisingEvents = false; fsw.Dispose(); }
        _watchers.Clear();
        _cts?.Cancel(); _cts?.Dispose();
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }
    }
}
