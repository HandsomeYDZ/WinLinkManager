using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using WinLinkManager.Core.Models;
using WinLinkManager.Core.Native;

namespace WinLinkManager.Core.Services;

/// <summary>
/// 符号链接操作服务：创建、删除、类型检测与转换
/// </summary>
public class LinkService : ILinkService
{
    // Win32 常量定义
    private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
    private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    private const uint IO_REPARSE_TAG_SYMLINK = 0xA000000C;
    private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;

    // SYMBOLIC_LINK_FLAG values for CreateSymbolicLinkW
    private const int SYMLINK_FLAG_FILE = 0;
    private const int SYMLINK_FLAG_DIR = 1;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateSymbolicLinkW(string lpSymlinkFileName, string lpTargetFileName, int dwFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFileAttributesW(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int fileInformationClass,
        out FileAttributeTagInfo lpFileInformation,
        uint dwBufferSize);

    [DllImport("kernel32.dll")]
    private static extern uint GetLastError();

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const int FILE_ATTRIBUTE_TAG_INFO_CLASS = 9;

    private readonly IIndexService _indexService;
    private readonly ILogger<LinkService>? _logger; // 为 null 时跳过日志记录

    public LinkService(IIndexService indexService, ILogger<LinkService>? logger = null)
    {
        _indexService = indexService;
        _logger = logger;
    }

    /// <summary>根据类型创建符号链接（文件/目录/Junction）</summary>
    public bool CreateLink(string linkPath, string targetPath, LinkType type)
    {
        try
        {
            bool result = type switch
            {
                LinkType.FileLink => CreateSymbolicLinkW(linkPath, targetPath, SYMLINK_FLAG_FILE),
                LinkType.DirectoryLink => CreateSymbolicLinkW(linkPath, targetPath, SYMLINK_FLAG_DIR),
                LinkType.Junction => CreateJunction(linkPath, targetPath),
                _ => false
            };

            // 创建失败时记录 Win32 错误码
            if (!result)
                _logger?.LogWarning("CreateLink 失败 (LastError={Error}): {LinkPath} -> {TargetPath} 类型={Type}",
                    GetLastError(), linkPath, targetPath, type);

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "创建符号链接异常: {LinkPath} -> {TargetPath}", linkPath, targetPath);
            return false;
        }
    }

    /// <summary>删除符号链接（目录型优先尝试 Directory.Delete）</summary>
    public void DeleteLink(string linkPath, LinkType type)
    {
        try
        {
            if (type == LinkType.DirectoryLink || type == LinkType.Junction)
            {
                try
                {
                    Directory.Delete(linkPath);
                }
                catch
                {
                    File.Delete(linkPath);
                }
            }
            else
            {
                File.Delete(linkPath);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "删除符号链接失败: {LinkPath}", linkPath);
            throw;
        }
    }

    /// <summary>转换链接类型，含备份-删除-重建-清理的安全流程</summary>
    public ConvertResult ConvertType(string linkPath, LinkType currentType, LinkType newType, string newTarget)
    {
        // Guard: 文件符号链接不支持类型转换
        if (currentType == LinkType.FileLink)
        {
            return new ConvertResult
            {
                Success = false,
                ErrorMessage = "文件符号链接不支持类型转换"
            };
        }

        // Guard: 已是目标类型
        if (currentType == newType)
        {
            return new ConvertResult
            {
                Success = false,
                ErrorMessage = "已是目标类型"
            };
        }

        // Step 1: 获取当前目标路径
        var currentTarget = newTarget;
        if (string.IsNullOrEmpty(currentTarget))
        {
            try
            {
                using var handle = CreateFileW(linkPath, GENERIC_READ, FILE_SHARE_READ,
                    IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
                if (!handle.IsInvalid)
                {
                    var target = NtfsNative.ResolveTarget(handle);
                    currentTarget = target ?? string.Empty;
                }
            }
            catch
            {
                // 无法解析目标路径
            }

            if (string.IsNullOrEmpty(currentTarget))
            {
                return new ConvertResult
                {
                    Success = false,
                    ErrorMessage = "无法解析当前链接目标路径"
                };
            }
        }

        // Step 2: 创建备份交接点
        var backupPath = linkPath + "_backup_" + DateTime.Now.Ticks;
        if (!CreateLink(backupPath, currentTarget, LinkType.Junction))
        {
            return new ConvertResult
            {
                Success = false,
                ErrorMessage = "创建备份交接点失败"
            };
        }

        // Step 3: 删除当前链接
        DeleteLink(linkPath, currentType);

        // Step 4: 创建新类型链接
        if (!CreateLink(linkPath, currentTarget, newType))
        {
            // 恢复备份
            try
            {
                Directory.Move(backupPath, linkPath);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "转换失败且恢复备份失败: {LinkPath}", linkPath);
            }

            return new ConvertResult
            {
                Success = false,
                ErrorMessage = "创建新类型链接失败，已恢复备份"
            };
        }

        // 创建 API 返回成功后仍从重解析点回读，只有磁盘上的真实类型吻合才算成功。
        var actualType = DetectType(linkPath);
        if (actualType != newType)
        {
            try
            {
                DeleteLink(linkPath, actualType);
                Directory.Move(backupPath, linkPath);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "类型校验失败且恢复备份失败: {LinkPath}", linkPath);
            }

            return new ConvertResult
            {
                Success = false,
                ErrorMessage = $"类型校验失败：请求 {newType}，实际 {actualType}"
            };
        }

        // Step 5: 删除备份（尽力而为）
        try
        {
            DeleteLink(backupPath, LinkType.Junction);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "删除备份交接点失败: {BackupPath}", backupPath);
        }

        return new ConvertResult { Success = true };
    }

    /// <summary>检测路径上的链接类型，通过读取重解析点标识判断</summary>
    public LinkType DetectType(string linkPath)
    {
        var attrs = GetFileAttributesW(linkPath);

        // 直接打开重解析点本身，因此即使目标已失效也仍能读取真实类型。
        using var handle = CreateFileW(
            linkPath,
            0,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);

        if (handle.IsInvalid)
            return Directory.Exists(linkPath) ? LinkType.DirectoryLink : LinkType.FileLink;

        if (GetFileInformationByHandleEx(handle, FILE_ATTRIBUTE_TAG_INFO_CLASS,
                out var tagInfo, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
        {
            attrs = tagInfo.FileAttributes;
            if (tagInfo.ReparseTag == IO_REPARSE_TAG_MOUNT_POINT)
                return LinkType.Junction;
            if (tagInfo.ReparseTag == IO_REPARSE_TAG_SYMLINK)
                return (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0
                    ? LinkType.DirectoryLink
                    : LinkType.FileLink;
        }

        if (attrs != INVALID_FILE_ATTRIBUTES && (attrs & FILE_ATTRIBUTE_REPARSE_POINT) == 0)
            return (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0
                ? LinkType.DirectoryLink
                : LinkType.FileLink;

        // 兼容回退：读取完整重解析缓冲区中的 Tag。
        const int bufferSize = 16384;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            if (DeviceIoControl(handle, FSCTL_GET_REPARSE_POINT,
                    IntPtr.Zero, 0, buffer, bufferSize, out _, IntPtr.Zero))
            {
                var tag = (uint)Marshal.ReadInt32(buffer);

                if (tag == IO_REPARSE_TAG_MOUNT_POINT)
                    return LinkType.Junction;

                if (tag == IO_REPARSE_TAG_SYMLINK)
                    return (attrs != INVALID_FILE_ATTRIBUTES && (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0) ||
                           Directory.Exists(linkPath)
                        ? LinkType.DirectoryLink
                        : LinkType.FileLink;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return Directory.Exists(linkPath) ? LinkType.DirectoryLink : LinkType.FileLink;
    }

    /// <summary>检查路径是否存在</summary>
    public bool Exists(string linkPath) => File.Exists(linkPath) || Directory.Exists(linkPath);

    /// <summary>通过 MOUNT_POINT 重解析点创建真正的交接点。</summary>
    private bool CreateJunction(string linkPath, string targetPath)
    {
        try
        {
            targetPath = GetAbsoluteTargetPath(linkPath, targetPath);

            if (!Directory.Exists(linkPath))
                Directory.CreateDirectory(linkPath);

            using var handle = CreateFileW(
                linkPath,
                GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                try { Directory.Delete(linkPath); } catch { }
                return false;
            }

            // 准备 MOUNT_POINT_REPARSE_DATA_BUFFER
            var substituteName = targetPath.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\??\UNC\" + targetPath.TrimStart('\\')
                : @"\??\" + targetPath;
            var substituteBytes = Encoding.Unicode.GetBytes(substituteName);
            var printNameBytes = Encoding.Unicode.GetBytes(targetPath);

            ushort substituteNameOffset = 0;
            ushort substituteNameLength = (ushort)substituteBytes.Length;
            ushort printNameOffset = (ushort)(substituteNameLength + sizeof(char));
            ushort printNameLength = (ushort)printNameBytes.Length;

            // ReparseTag(4) + ReparseDataLength(2) + Reserved(2) +
            // SubstituteNameOffset(2) + SubstituteNameLength(2) +
            // PrintNameOffset(2) + PrintNameLength(2) = 16 bytes header
            var pathBufferLength = substituteNameLength + sizeof(char) + printNameLength + sizeof(char);
            var reparseDataLength = pathBufferLength + 8;
            var totalSize = reparseDataLength + 8;
            var buf = Marshal.AllocHGlobal(totalSize);

            try
            {
                var pathOffset = 16;
                // 两个名称都以 NUL 分隔/结尾；Length 字段本身不包含 NUL。
                for (var i = 0; i < totalSize; i++)
                    Marshal.WriteByte(buf, i, 0);
                Marshal.WriteInt32(buf, 0, unchecked((int)IO_REPARSE_TAG_MOUNT_POINT));
                Marshal.WriteInt16(buf, 4, (short)reparseDataLength);
                Marshal.WriteInt16(buf, 6, 0);
                Marshal.WriteInt16(buf, 8, (short)substituteNameOffset);
                Marshal.WriteInt16(buf, 10, (short)substituteNameLength);
                Marshal.WriteInt16(buf, 12, (short)printNameOffset);
                Marshal.WriteInt16(buf, 14, (short)printNameLength);
                Marshal.Copy(substituteBytes, 0, buf + pathOffset, substituteBytes.Length);
                Marshal.Copy(printNameBytes, 0, buf + pathOffset + printNameOffset, printNameBytes.Length);

                var success = DeviceIoControl(handle, FSCTL_SET_REPARSE_POINT,
                    buf, (uint)totalSize, IntPtr.Zero, 0, out _, IntPtr.Zero);

                if (!success)
                {
                    try { Directory.Delete(linkPath); } catch { }
                }

                return success;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "CreateJunctionViaDeviceIoControl 失败: {LinkPath} -> {TargetPath}", linkPath, targetPath);
            try { Directory.Delete(linkPath); } catch { }
            return false;
        }
    }

    /// <summary>交接点必须保存绝对目标；相对输入按链接所在目录解析。</summary>
    private static string GetAbsoluteTargetPath(string linkPath, string targetPath)
    {
        string fullPath;
        if (Path.IsPathRooted(targetPath))
        {
            fullPath = Path.GetFullPath(targetPath);
        }
        else
        {
            var linkDirectory = Path.GetDirectoryName(Path.GetFullPath(linkPath));
            if (string.IsNullOrEmpty(linkDirectory))
                throw new ArgumentException("无法确定链接所在目录", nameof(linkPath));

            fullPath = Path.GetFullPath(Path.Combine(linkDirectory, targetPath));
        }

        if (fullPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + fullPath.Substring(8);
        if (fullPath.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            return fullPath.Substring(4);
        return fullPath;
    }
}
