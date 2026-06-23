using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Microsoft.Extensions.Logging;
using WinLinkManager.Core.Data;
using WinLinkManager.Core.Models;
using WinLinkManager.Core.Native;

namespace WinLinkManager.Core.Services;

/// <summary>
/// NTFS MFT 扫描器，通过 USN 日志高效枚举所有重解析点
/// </summary>
public class MftScannerService : IScannerService
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<MftScannerService> _logger;

    public MftScannerService(
        AppDbContext dbContext,
        ILogger<MftScannerService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>全量扫描：按配置遍历包含的根目录，流式返回发现的符号链接</summary>
    public async IAsyncEnumerable<LinkEntry> FullScanAsync(
        List<ScanDirectoryConfig> scanDirs,
        IProgress<ScanProgress>? progress,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // 扫描由调用方安排在后台线程；先异步让步，避免同步完成的“假异步”迭代器。
        await Task.Yield();

        // 构建排除路径集合和包含路径列表
        var excluded = scanDirs
            .Where(d => d.IsExcluded)
            .Select(d => NormalizeRootPath(d.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var included = scanDirs
            .Where(d => !d.IsExcluded)
            .Select(d => NormalizeRootPath(d.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 若无包含路径，默认扫描所有固定硬盘根目录
        if (!included.Any())
        {
            included = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => NormalizeRootPath(d.Name))
                .ToList();
        }

        var counters = new ScanCounters(); // 扫描计数保持器

        // 依次扫描每个包含的根目录
        foreach (var rootDir in included)
        {
            ct.ThrowIfCancellationRequested();
            if (IsExcluded(rootDir, excluded))
                continue;

            foreach (var entry in EnumerateReparsePointsOnVolume(rootDir, excluded, progress, counters, ct))
            {
                yield return entry;
            }
        }

        // 报告扫描完成
        progress?.Report(new ScanProgress
        {
            TotalScanned = counters.TotalScanned,
            LinksFound = counters.LinksFound,
            IsComplete = true
        });
    }

    /// <summary>将路径标准化为以分隔符结尾的根目录格式</summary>
    private static string NormalizeRootPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var root = Path.GetPathRoot(path) ?? path;
        return root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    /// <summary>扫描计数器（可变字段，避免装箱）</summary>
    private sealed class ScanCounters
    {
        public long TotalScanned;
        public long LinksFound;
    }

    /// <summary>通过 USN 日志枚举卷上的重解析点，失败时回退到文件系统遍历</summary>
    private IEnumerable<LinkEntry> EnumerateReparsePointsOnVolume(
        string rootDir,
        HashSet<string> excluded,
        IProgress<ScanProgress>? progress,
        ScanCounters counters,
        CancellationToken ct)
    {
        // 打开卷句柄（如失败则回退到目录遍历）
        using var volumeHandle = OpenVolumeHandle(rootDir);
        if (volumeHandle is null || volumeHandle.IsInvalid)
        {
            _logger.LogWarning("Failed to open volume for scanning: {RootDir}, falling back to filesystem traversal", rootDir);
            foreach (var fallbackEntry in ScanFileSystemTree(rootDir, excluded, progress, counters, ct))
                yield return fallbackEntry;
            yield break;
        }

        // 目录映射表（FRN -> 名称/父FRN）和重解析点候选列表
        var directories = new Dictionary<ulong, DirectoryInfoEntry>();
        var reparseCandidates = new List<ReparseCandidate>();

        const int bufferSize = 4 * 1024 * 1024; // 4MB 输出缓冲区
        var outBuffer = Marshal.AllocHGlobal(bufferSize);
        var inSize = Marshal.SizeOf<NtfsNative.MFT_ENUM_DATA_V1>();
        var inBuffer = Marshal.AllocHGlobal(inSize);

        try
        {
            // 从 MFT 开始处枚举所有 USN 记录
            var enumData = new NtfsNative.MFT_ENUM_DATA_V1
            {
                StartFileReferenceNumber = 0,
                LowUsn = 0,
                HighUsn = long.MaxValue
            };
            Marshal.StructureToPtr(enumData, inBuffer, false);

            // 分页遍历 USN 日志（每批读满缓冲区后继续下一页）
            while (!ct.IsCancellationRequested)
            {
                var success = NtfsNative.DeviceIoControl(
                    volumeHandle,
                    NtfsNative.FSCTL_ENUM_USN_DATA,
                    inBuffer,
                    (uint)inSize,
                    outBuffer,
                    (uint)bufferSize,
                    out var bytesReturned,
                    IntPtr.Zero);

                if (!success)
                {
                    var error = Marshal.GetLastWin32Error();
                    // ERROR_HANDLE_EOF(38) / ERROR_MORE_DATA(234) 表示枚举完成
                    if (error == 38 || error == 234)
                    {
                        break;
                    }

                    _logger.LogWarning("FSCTL_ENUM_USN_DATA failed on {RootDir} with error {Error}, falling back to filesystem traversal", rootDir, error);
                    foreach (var fallbackEntry in ScanFileSystemTree(rootDir, excluded, progress, counters, ct))
                        yield return fallbackEntry;
                    yield break;
                }

                // 解析批量返回的 USN 记录
                var nextFileReferenceNumber = (ulong)Marshal.ReadInt64(outBuffer);
                var recordPtr = IntPtr.Add(outBuffer, 8);
                var bytesRemaining = (int)bytesReturned - 8;

                while (bytesRemaining > 0)
                {
                    var recordLength = (uint)Marshal.ReadInt32(recordPtr);
                    if (recordLength < 64 || recordLength > bytesRemaining)
                        break;

                    var record = ParseUsnRecord(recordPtr);
                    counters.TotalScanned++;

                    // 每 1024 条报告一次进度
                    if ((counters.TotalScanned & 0x3FF) == 0)
                    {
                        progress?.Report(new ScanProgress
                        {
                            TotalScanned = counters.TotalScanned,
                            LinksFound = counters.LinksFound,
                            CurrentDirectory = rootDir
                        });
                    }

                    // 记录目录信息用于后续路径拼接
                    if (record.IsDirectory)
                    {
                        directories[record.FileReferenceNumber] = new DirectoryInfoEntry(
                            record.ParentFileReferenceNumber,
                            record.FileName);
                    }

                    // 收集重解析点候选（含符号链接和交接点）
                    if (record.IsReparsePoint)
                    {
                        reparseCandidates.Add(new ReparseCandidate(
                            record.FileReferenceNumber,
                            record.ParentFileReferenceNumber,
                            record.FileName,
                            record.FileAttributes));
                    }

                    recordPtr = IntPtr.Add(recordPtr, (int)recordLength);
                    bytesRemaining -= (int)recordLength;
                }

                if (nextFileReferenceNumber == 0)
                    break;

                // 设置下一页起始 FRN
                Marshal.WriteInt64(inBuffer, (long)nextFileReferenceNumber);
            }

            // 遍历所有重解析点候选，拼接完整路径并分析
            foreach (var candidate in reparseCandidates)
            {
                ct.ThrowIfCancellationRequested();

                if (!TryBuildFullPath(rootDir, candidate, directories, out var fullPath))
                    continue;

                if (IsExcluded(fullPath, excluded))
                    continue;

                var entry = AnalyzeReparsePoint(fullPath);
                if (entry != null)
                {
                    counters.LinksFound++;
                    progress?.Report(new ScanProgress
                    {
                        TotalScanned = counters.TotalScanned,
                        LinksFound = counters.LinksFound,
                        CurrentDirectory = fullPath
                    });
                    yield return entry;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(outBuffer);
            Marshal.FreeHGlobal(inBuffer);
        }
    }

    /// <summary>打开卷句柄用于 DeviceIoControl</summary>
    private static SafeFileHandle OpenVolumeHandle(string rootDir)
    {
        var pathRoot = Path.GetPathRoot(rootDir);
        if (string.IsNullOrEmpty(pathRoot))
            return new SafeFileHandle(new IntPtr(-1), true);

        var volumePath = @"\\.\" + pathRoot.TrimEnd(Path.DirectorySeparatorChar);
        return NtfsNative.CreateFileW(
            volumePath,
            NtfsNative.GENERIC_READ,
            NtfsNative.FILE_SHARE_READ | NtfsNative.FILE_SHARE_WRITE | NtfsNative.FILE_SHARE_DELETE,
            IntPtr.Zero,
            NtfsNative.OPEN_EXISTING,
            NtfsNative.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);
    }

    /// <summary>通过 FRN 目录映射表回溯父目录，拼接完整路径</summary>
    private static bool TryBuildFullPath(
        string rootDir,
        ReparseCandidate candidate,
        Dictionary<ulong, DirectoryInfoEntry> directories,
        out string fullPath)
    {
        var segments = new List<string> { candidate.FileName };
        var parent = candidate.ParentFileReferenceNumber;

        while (parent != 0 && directories.TryGetValue(parent, out var info))
        {
            if (info.ParentFileReferenceNumber == parent)
                break;

            segments.Insert(0, info.Name);
            parent = info.ParentFileReferenceNumber;
        }

        if (segments.Count == 0)
        {
            fullPath = string.Empty;
            return false;
        }

        fullPath = Path.Combine(rootDir, Path.Combine(segments.ToArray()));
        return true;
    }

    /// <summary>从非托管内存解析单条 USN 记录</summary>
    private static ParsedUsnRecord ParseUsnRecord(nint recordPtr)
    {
        var fileReferenceNumber = (ulong)Marshal.ReadInt64(recordPtr, 8);
        var parentFileReferenceNumber = (ulong)Marshal.ReadInt64(recordPtr, 16);
        var fileAttributes = (uint)Marshal.ReadInt32(recordPtr, 52);
        var fileNameLength = (ushort)Marshal.ReadInt16(recordPtr, 56);
        var fileNameOffset = (ushort)Marshal.ReadInt16(recordPtr, 58);
        var namePtr = IntPtr.Add(recordPtr, fileNameOffset);
        var fileName = Marshal.PtrToStringUni(namePtr, fileNameLength / 2) ?? string.Empty;
        return new ParsedUsnRecord(fileReferenceNumber, parentFileReferenceNumber, fileAttributes, fileName);
    }

    /// <summary>目录信息：FRN 对应的父 FRN 和名称</summary>
    private sealed record DirectoryInfoEntry(ulong ParentFileReferenceNumber, string Name);

    /// <summary>重解析点候选记录</summary>
    private sealed record ReparseCandidate(
        ulong FileReferenceNumber,
        ulong ParentFileReferenceNumber,
        string FileName,
        uint FileAttributes)
    {
        public bool IsDirectory => (FileAttributes & (uint)System.IO.FileAttributes.Directory) != 0;
        public bool IsReparsePoint => (FileAttributes & NtfsNative.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
    }

    /// <summary>解析后的 USN 记录</summary>
    private sealed record ParsedUsnRecord(
        ulong FileReferenceNumber,
        ulong ParentFileReferenceNumber,
        uint FileAttributes,
        string FileName)
    {
        public bool IsDirectory => (FileAttributes & (uint)System.IO.FileAttributes.Directory) != 0;
        public bool IsReparsePoint => (FileAttributes & NtfsNative.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
    }

    /// <summary>回退方案：通过广度优先遍历文件系统树查找符号链接</summary>
    private IEnumerable<LinkEntry> ScanFileSystemTree(
        string rootDir,
        HashSet<string> excluded,
        IProgress<ScanProgress>? progress,
        ScanCounters counters,
        CancellationToken ct)
    {
        var queue = new Queue<string>();
        queue.Enqueue(rootDir);

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var currentDir = queue.Dequeue();

            if (IsExcluded(currentDir, excluded))
                continue;

            progress?.Report(new ScanProgress
            {
                TotalScanned = counters.TotalScanned,
                LinksFound = counters.LinksFound,
                CurrentDirectory = currentDir
            });

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(currentDir);
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogDebug("Access denied: {Dir}", currentDir);
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                counters.TotalScanned++;
                ct.ThrowIfCancellationRequested();

                LinkEntry? entryResult = null;
                try
                {
                    var attr = File.GetAttributes(entry);
                    // 目录入队继续遍历
                    if (attr.HasFlag(System.IO.FileAttributes.Directory))
                    {
                        queue.Enqueue(entry);
                    }

                    // 重解析点则分析其链接信息
                    if (attr.HasFlag(System.IO.FileAttributes.ReparsePoint))
                    {
                        entryResult = AnalyzeReparsePoint(entry);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (entryResult != null)
                {
                    counters.LinksFound++;
                    progress?.Report(new ScanProgress
                    {
                        TotalScanned = counters.TotalScanned,
                        LinksFound = counters.LinksFound,
                        CurrentDirectory = entry
                    });
                    yield return entryResult;
                }
            }
        }
    }

    /// <summary>分析路径上的重解析点，读取链接类型和目标路径</summary>
    private static LinkEntry? AnalyzeReparsePoint(string path)
    {
        uint flags = NtfsNative.FILE_FLAG_BACKUP_SEMANTICS |
                     NtfsNative.FILE_FLAG_OPEN_REPARSE_POINT |
                     NtfsNative.FILE_ATTRIBUTE_NORMAL;

        using var handle = NtfsNative.CreateFileW(
            path,
            NtfsNative.GENERIC_READ,
            NtfsNative.FILE_SHARE_READ | NtfsNative.FILE_SHARE_WRITE | NtfsNative.FILE_SHARE_DELETE,
            IntPtr.Zero,
            NtfsNative.OPEN_EXISTING,
            flags,
            IntPtr.Zero);

        if (handle.IsInvalid)
            return null;

        var reparseBuffer = Marshal.AllocHGlobal(NtfsNative.MAX_REPARSE_DATA_BUFFER_SIZE);
        try
        {
            if (!NtfsNative.DeviceIoControl(
                    handle,
                    NtfsNative.FSCTL_GET_REPARSE_POINT,
                    IntPtr.Zero,
                    0,
                    reparseBuffer,
                    NtfsNative.MAX_REPARSE_DATA_BUFFER_SIZE,
                    out _,
                    IntPtr.Zero))
                return null;

            // 根据重解析标记判断链接类型
            uint reparseTag = (uint)Marshal.ReadInt32(reparseBuffer);
            LinkType linkType;
            string target;

            if (reparseTag == NtfsNative.IO_REPARSE_TAG_SYMLINK)
            {
                var attr = File.GetAttributes(path);
                bool isDir = attr.HasFlag(FileAttributes.Directory);
                linkType = isDir ? LinkType.DirectoryLink : LinkType.FileLink;
                target = NtfsNative.ReadSubstituteName(reparseBuffer, reparseTag);
            }
            else if (reparseTag == NtfsNative.IO_REPARSE_TAG_MOUNT_POINT)
            {
                linkType = LinkType.Junction;
                target = NtfsNative.ReadSubstituteName(reparseBuffer, reparseTag);
            }
            else
            {
                return null; // 未知重解析标记，跳过
            }

            // 将 NT 路径格式转换为 Win32 路径。符号链接的重解析数据可以保存
            // 相对目标；Windows 会以链接所在目录为基准解析，索引中也应保存同一
            // 个完整路径，避免错误显示为“失效”以及复制出缺少前缀的路径。
            target = NtfsNative.NtToWin32Path(target);
            target = ResolveTargetPath(path, target);
            var linkName = Path.GetFileName(path);
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string creationTime;

            try
            {
                creationTime = File.GetCreationTime(path).ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch
            {
                creationTime = now;
            }

            // Determine if target is valid (accessible)
            var status = CheckTargetValid(target) ? LinkStatus.Valid : LinkStatus.Broken;

            return new LinkEntry
            {
                LinkPath = path,
                LinkName = linkName,
                TargetPath = target,
                LinkType = linkType,
                CreationTime = creationTime,
                Status = status,
                LastSeenTime = now
            };
        }
        finally
        {
            Marshal.FreeHGlobal(reparseBuffer);
        }
    }

    /// <summary>检查链接目标路径是否仍可访问</summary>
    private static bool CheckTargetValid(string target)
    {
        try
        {
            if (target.EndsWith(Path.DirectorySeparatorChar.ToString()) ||
                target.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
                target = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return Directory.Exists(target) || File.Exists(target);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>将重解析点中的相对目标按链接所在目录解析为完整路径。</summary>
    private static string ResolveTargetPath(string linkPath, string target)
    {
        if (string.IsNullOrWhiteSpace(target) || Path.IsPathRooted(target))
            return target;

        try
        {
            var linkDirectory = Path.GetDirectoryName(linkPath);
            return string.IsNullOrEmpty(linkDirectory)
                ? target
                : Path.GetFullPath(Path.Combine(linkDirectory, target));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return target;
        }
    }

    /// <summary>检查目录是否在排除列表中（含子目录匹配）</summary>
    private static bool IsExcluded(string dir, HashSet<string> excluded)
    {
        var normalized = dir.TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
        if (excluded.Contains(normalized))
            return true;

        // Check if dir is a subdirectory of any excluded path
        foreach (var ex in excluded)
        {
            if (normalized.StartsWith(ex + Path.DirectorySeparatorChar) ||
                normalized == ex)
                return true;
        }
        return false;
    }
}
