using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using SymlinkManager.Core.Models;

namespace SymlinkManager.Core.Services;

public class SymlinkService : ISymlinkService
{
    // Win32 constants
    private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    private const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
    private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    private const uint IO_REPARSE_TAG_SYMLINK = 0xA000000C;
    private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
    private const uint SYMLINK_FLAG_DIRECTORY = 0x00000001;

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

    [DllImport("kernel32.dll")]
    private static extern uint GetLastError();

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    private readonly IIndexService _indexService;
    private readonly ILogger<SymlinkService>? _logger;

    public SymlinkService(IIndexService indexService, ILogger<SymlinkService>? logger = null)
    {
        _indexService = indexService;
        _logger = logger;
    }

    public SymlinkService(IIndexService indexService) : this(indexService, null) { }

    public bool CreateSymlink(string linkPath, string targetPath, SymlinkType type)
    {
        try
        {
            bool result = type switch
            {
                SymlinkType.FileSymlink => CreateSymbolicLinkW(linkPath, targetPath, SYMLINK_FLAG_FILE),
                SymlinkType.DirectorySymlink => CreateSymbolicLinkW(linkPath, targetPath, SYMLINK_FLAG_DIR),
                SymlinkType.Junction => CreateJunction(linkPath, targetPath),
                _ => false
            };

            if (!result)
                _logger?.LogWarning("CreateSymlink 失败 (LastError={Error}): {LinkPath} -> {TargetPath} 类型={Type}",
                    GetLastError(), linkPath, targetPath, type);

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "创建符号链接异常: {LinkPath} -> {TargetPath}", linkPath, targetPath);
            return false;
        }
    }

    public void DeleteSymlink(string linkPath, SymlinkType type)
    {
        try
        {
            if (type == SymlinkType.DirectorySymlink || type == SymlinkType.Junction)
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

    public ConvertResult ConvertType(string linkPath, SymlinkType currentType, SymlinkType newType, string newTarget)
    {
        // Guard: 文件符号链接不支持类型转换
        if (currentType == SymlinkType.FileSymlink)
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
                var target = Directory.ResolveLinkTarget(linkPath, false);
                currentTarget = target?.FullName ?? string.Empty;
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
        if (!CreateSymlink(backupPath, currentTarget, SymlinkType.Junction))
        {
            return new ConvertResult
            {
                Success = false,
                ErrorMessage = "创建备份交接点失败"
            };
        }

        // Step 3: 删除当前链接
        DeleteSymlink(linkPath, currentType);

        // Step 4: 创建新类型链接
        if (!CreateSymlink(linkPath, currentTarget, newType))
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

        // Step 5: 删除备份（尽力而为）
        try
        {
            DeleteSymlink(backupPath, SymlinkType.Junction);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "删除备份交接点失败: {BackupPath}", backupPath);
        }

        return new ConvertResult { Success = true };
    }

    public SymlinkType DetectType(string linkPath)
    {
        if (!Exists(linkPath))
            return SymlinkType.FileSymlink;

        var attrs = GetFileAttributesW(linkPath);
        if (attrs == INVALID_FILE_ATTRIBUTES || (attrs & FILE_ATTRIBUTE_REPARSE_POINT) == 0)
        {
            return Directory.Exists(linkPath) ? SymlinkType.DirectorySymlink : SymlinkType.FileSymlink;
        }

        // Read reparse point data via DeviceIoControl
        using var handle = CreateFileW(
            linkPath,
            GENERIC_READ,
            FILE_SHARE_READ,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);

        if (handle.IsInvalid)
            return Directory.Exists(linkPath) ? SymlinkType.DirectorySymlink : SymlinkType.FileSymlink;

        const int bufferSize = 16384;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            if (DeviceIoControl(handle, FSCTL_GET_REPARSE_POINT,
                    IntPtr.Zero, 0, buffer, bufferSize, out _, IntPtr.Zero))
            {
                var tag = (uint)Marshal.ReadInt32(buffer);

                if (tag == IO_REPARSE_TAG_MOUNT_POINT)
                    return SymlinkType.Junction;

                if (tag == IO_REPARSE_TAG_SYMLINK)
                {
                    // SymbolicLinkReparseBuffer.Flags is at offset 16
                    var flags = Marshal.ReadInt32(buffer, 16);
                    if ((flags & SYMLINK_FLAG_DIRECTORY) != 0)
                        return SymlinkType.DirectorySymlink;
                    return SymlinkType.FileSymlink;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return Directory.Exists(linkPath) ? SymlinkType.DirectorySymlink : SymlinkType.FileSymlink;
    }

    public bool Exists(string linkPath) => File.Exists(linkPath) || Directory.Exists(linkPath);

    private bool CreateJunction(string linkPath, string targetPath)
    {
        // 优先使用目录符号链接（Win10+ 开发者模式下可用）
        if (CreateSymbolicLinkW(linkPath, targetPath, SYMLINK_FLAG_DIR))
            return true;

        // 回退: 通过 DeviceIoControl 创建交接点
        try
        {
            if (!Directory.Exists(linkPath))
                Directory.CreateDirectory(linkPath);

            using var handle = CreateFileW(
                linkPath,
                GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
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
            var substituteName = @"\??\" + targetPath;
            var substituteBytes = Encoding.Unicode.GetBytes(substituteName);
            var printNameBytes = Encoding.Unicode.GetBytes(targetPath);

            ushort substituteNameOffset = 0;
            ushort substituteNameLength = (ushort)substituteBytes.Length;
            ushort printNameOffset = substituteNameLength;
            ushort printNameLength = (ushort)printNameBytes.Length;

            // ReparseTag(4) + ReparseDataLength(2) + Reserved(2) +
            // SubstituteNameOffset(2) + SubstituteNameLength(2) +
            // PrintNameOffset(2) + PrintNameLength(2) = 16 bytes header
            var reparseDataLength = substituteNameLength + printNameLength + 12;
            var totalSize = reparseDataLength + 8;
            var buf = Marshal.AllocHGlobal(totalSize);

            try
            {
                Marshal.WriteInt32(buf, 0, unchecked((int)IO_REPARSE_TAG_MOUNT_POINT));
                Marshal.WriteInt16(buf, 4, (short)reparseDataLength);
                Marshal.WriteInt16(buf, 6, 0); // Reserved
                Marshal.WriteInt16(buf, 8, (short)substituteNameOffset);
                Marshal.WriteInt16(buf, 10, (short)substituteNameLength);
                Marshal.WriteInt16(buf, 12, (short)printNameOffset);
                Marshal.WriteInt16(buf, 14, (short)printNameLength);

                var pathOffset = 16;
                Marshal.Copy(substituteBytes, 0, buf + pathOffset, substituteBytes.Length);
                Marshal.Copy(printNameBytes, 0, buf + pathOffset + substituteNameLength, printNameBytes.Length);

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
}
