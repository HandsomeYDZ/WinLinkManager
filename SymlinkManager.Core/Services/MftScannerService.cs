using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Microsoft.Extensions.Logging;
using SymlinkManager.Core.Data;
using SymlinkManager.Core.Models;
using SymlinkManager.Core.Native;

namespace SymlinkManager.Core.Services;

public class MftScannerService : IScannerService
{
    private readonly VolumeHandleService _volumeService;
    private readonly AppDbContext _dbContext;
    private readonly ILogger<MftScannerService> _logger;

    public MftScannerService(
        VolumeHandleService volumeService,
        AppDbContext dbContext,
        ILogger<MftScannerService> logger)
    {
        _volumeService = volumeService;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async IAsyncEnumerable<SymlinkEntry> FullScanAsync(
        List<ScanDirectoryConfig> scanDirs,
        IProgress<ScanProgress>? progress,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var excluded = scanDirs
            .Where(d => d.IsExcluded)
            .Select(d => NormalizeRootPath(d.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var included = scanDirs
            .Where(d => !d.IsExcluded)
            .Select(d => NormalizeRootPath(d.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!included.Any())
        {
            included = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => NormalizeRootPath(d.Name))
                .ToList();
        }

        var counters = new ScanCounters();

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

        progress?.Report(new ScanProgress
        {
            TotalScanned = counters.TotalScanned,
            LinksFound = counters.LinksFound,
            IsComplete = true
        });
    }

    private static string NormalizeRootPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var root = Path.GetPathRoot(path) ?? path;
        return root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    private sealed class ScanCounters
    {
        public long TotalScanned;
        public long LinksFound;
    }

    private IEnumerable<SymlinkEntry> EnumerateReparsePointsOnVolume(
        string rootDir,
        HashSet<string> excluded,
        IProgress<ScanProgress>? progress,
        ScanCounters counters,
        CancellationToken ct)
    {
        using var volumeHandle = OpenVolumeHandle(rootDir);
        if (volumeHandle is null || volumeHandle.IsInvalid)
        {
            _logger.LogWarning("Failed to open volume for scanning: {RootDir}, falling back to filesystem traversal", rootDir);
            foreach (var fallbackEntry in ScanFileSystemTree(rootDir, excluded, progress, counters, ct))
                yield return fallbackEntry;
            yield break;
        }

        var directories = new Dictionary<ulong, DirectoryInfoEntry>();
        var reparseCandidates = new List<ReparseCandidate>();

        const int bufferSize = 4 * 1024 * 1024;
        var outBuffer = Marshal.AllocHGlobal(bufferSize);
        var inSize = Marshal.SizeOf<NtfsNative.MFT_ENUM_DATA_V1>();
        var inBuffer = Marshal.AllocHGlobal(inSize);

        try
        {
            var enumData = new NtfsNative.MFT_ENUM_DATA_V1
            {
                StartFileReferenceNumber = 0,
                LowUsn = 0,
                HighUsn = long.MaxValue
            };
            Marshal.StructureToPtr(enumData, inBuffer, false);

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
                    nint.Zero);

                if (!success)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 38 || error == 234)
                    {
                        break;
                    }

                    _logger.LogWarning("FSCTL_ENUM_USN_DATA failed on {RootDir} with error {Error}, falling back to filesystem traversal", rootDir, error);
                    foreach (var fallbackEntry in ScanFileSystemTree(rootDir, excluded, progress, counters, ct))
                        yield return fallbackEntry;
                    yield break;
                }

                var nextFileReferenceNumber = (ulong)Marshal.ReadInt64(outBuffer);
                var recordPtr = nint.Add(outBuffer, 8);
                var bytesRemaining = (int)bytesReturned - 8;

                while (bytesRemaining > 0)
                {
                    var recordLength = (uint)Marshal.ReadInt32(recordPtr);
                    if (recordLength < 64 || recordLength > bytesRemaining)
                        break;

                    var record = ParseUsnRecord(recordPtr);
                    counters.TotalScanned++;

                    if ((counters.TotalScanned & 0x3FF) == 0)
                    {
                        progress?.Report(new ScanProgress
                        {
                            TotalScanned = counters.TotalScanned,
                            LinksFound = counters.LinksFound,
                            CurrentDirectory = rootDir
                        });
                    }

                    if (record.IsDirectory)
                    {
                        directories[record.FileReferenceNumber] = new DirectoryInfoEntry(
                            record.ParentFileReferenceNumber,
                            record.FileName);
                    }

                    if (record.IsReparsePoint)
                    {
                        reparseCandidates.Add(new ReparseCandidate(
                            record.FileReferenceNumber,
                            record.ParentFileReferenceNumber,
                            record.FileName,
                            record.FileAttributes));
                    }

                    recordPtr = nint.Add(recordPtr, (int)recordLength);
                    bytesRemaining -= (int)recordLength;
                }

                if (nextFileReferenceNumber == 0)
                    break;

                Marshal.WriteInt64(inBuffer, (long)nextFileReferenceNumber);
            }

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

    private static SafeFileHandle OpenVolumeHandle(string rootDir)
    {
        var pathRoot = Path.GetPathRoot(rootDir);
        if (string.IsNullOrEmpty(pathRoot))
            return new SafeFileHandle(new nint(-1), true);

        var volumePath = @"\\.\" + pathRoot.TrimEnd(Path.DirectorySeparatorChar);
        return NtfsNative.CreateFileW(
            volumePath,
            NtfsNative.GENERIC_READ,
            NtfsNative.FILE_SHARE_READ | NtfsNative.FILE_SHARE_WRITE | NtfsNative.FILE_SHARE_DELETE,
            nint.Zero,
            NtfsNative.OPEN_EXISTING,
            NtfsNative.FILE_FLAG_BACKUP_SEMANTICS,
            nint.Zero);
    }

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

    private static ParsedUsnRecord ParseUsnRecord(nint recordPtr)
    {
        var fileReferenceNumber = (ulong)Marshal.ReadInt64(recordPtr, 8);
        var parentFileReferenceNumber = (ulong)Marshal.ReadInt64(recordPtr, 16);
        var fileAttributes = (uint)Marshal.ReadInt32(recordPtr, 52);
        var fileNameLength = (ushort)Marshal.ReadInt16(recordPtr, 56);
        var fileNameOffset = (ushort)Marshal.ReadInt16(recordPtr, 58);
        var namePtr = nint.Add(recordPtr, fileNameOffset);
        var fileName = Marshal.PtrToStringUni(namePtr, fileNameLength / 2) ?? string.Empty;
        return new ParsedUsnRecord(fileReferenceNumber, parentFileReferenceNumber, fileAttributes, fileName);
    }

    private sealed record DirectoryInfoEntry(ulong ParentFileReferenceNumber, string Name);

    private sealed record ReparseCandidate(
        ulong FileReferenceNumber,
        ulong ParentFileReferenceNumber,
        string FileName,
        uint FileAttributes)
    {
        public bool IsDirectory => (FileAttributes & (uint)System.IO.FileAttributes.Directory) != 0;
        public bool IsReparsePoint => (FileAttributes & NtfsNative.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
    }

    private sealed record ParsedUsnRecord(
        ulong FileReferenceNumber,
        ulong ParentFileReferenceNumber,
        uint FileAttributes,
        string FileName)
    {
        public bool IsDirectory => (FileAttributes & (uint)System.IO.FileAttributes.Directory) != 0;
        public bool IsReparsePoint => (FileAttributes & NtfsNative.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
    }

    private IEnumerable<SymlinkEntry> ScanFileSystemTree(
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

                SymlinkEntry? entryResult = null;
                try
                {
                    var attr = File.GetAttributes(entry);
                    if (attr.HasFlag(System.IO.FileAttributes.Directory))
                    {
                        queue.Enqueue(entry);
                    }

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

    private static SymlinkEntry? AnalyzeReparsePoint(string path)
    {
        uint flags = NtfsNative.FILE_FLAG_BACKUP_SEMANTICS |
                     NtfsNative.FILE_FLAG_OPEN_REPARSE_POINT |
                     NtfsNative.FILE_ATTRIBUTE_NORMAL;

        using var handle = NtfsNative.CreateFileW(
            path,
            NtfsNative.GENERIC_READ,
            NtfsNative.FILE_SHARE_READ | NtfsNative.FILE_SHARE_WRITE | NtfsNative.FILE_SHARE_DELETE,
            nint.Zero,
            NtfsNative.OPEN_EXISTING,
            flags,
            nint.Zero);

        if (handle.IsInvalid)
            return null;

        var reparseBuffer = Marshal.AllocHGlobal(NtfsNative.MAX_REPARSE_DATA_BUFFER_SIZE);
        try
        {
            if (!NtfsNative.DeviceIoControl(
                    handle,
                    NtfsNative.FSCTL_GET_REPARSE_POINT,
                    nint.Zero,
                    0,
                    reparseBuffer,
                    NtfsNative.MAX_REPARSE_DATA_BUFFER_SIZE,
                    out _,
                    nint.Zero))
                return null;

            uint reparseTag = (uint)Marshal.ReadInt32(reparseBuffer);
            SymlinkType linkType;
            string target;

            if (reparseTag == NtfsNative.IO_REPARSE_TAG_SYMLINK)
            {
                var attr = File.GetAttributes(path);
                bool isDir = attr.HasFlag(FileAttributes.Directory);
                linkType = isDir ? SymlinkType.DirectorySymlink : SymlinkType.FileSymlink;
                target = NtfsNative.ReadSubstituteName(reparseBuffer, reparseTag);
            }
            else if (reparseTag == NtfsNative.IO_REPARSE_TAG_MOUNT_POINT)
            {
                linkType = SymlinkType.Junction;
                target = NtfsNative.ReadSubstituteName(reparseBuffer, reparseTag);
            }
            else
            {
                return null; // Unknown reparse tag, skip
            }

            target = NtfsNative.NtToWin32Path(target);
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

            return new SymlinkEntry
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

    private static bool CheckTargetValid(string target)
    {
        try
        {
            if (target.EndsWith(Path.DirectorySeparatorChar) ||
                target.EndsWith(Path.AltDirectorySeparatorChar))
                target = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return Directory.Exists(target) || File.Exists(target);
        }
        catch
        {
            return false;
        }
    }

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
